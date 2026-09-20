using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Payments;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffPaymentsController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPaymentService _paymentService;
    private readonly IAuditService _auditService;

    public StaffPaymentsController(
        ApplicationDbContext dbContext,
        IPaymentService paymentService,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _paymentService = paymentService;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Reconciliation(
        CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetAdminPaymentsAsync(
            PaymentStatus.AwaitingConfirmation,
            null,
            cancellationToken);

        ViewBag.BundledDepositByBooking = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.AwaitingConfirmation)
            .GroupBy(payment => payment.BookingId)
            .Select(group => new
            {
                BookingId = group.Key,
                Amount = group.Sum(payment => payment.Amount)
            })
            .ToDictionaryAsync(
                item => item.BookingId,
                item => item.Amount,
                cancellationToken);

        return View(payments
            .Where(payment =>
                payment.Type != PaymentType.Refund &&
                payment.Status == PaymentStatus.AwaitingConfirmation)
            .OrderBy(payment => payment.PaymentId)
            .ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmQr(
        int paymentId,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.ConfirmQrPaymentAsync(
            paymentId,
            staffId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã đối soát và xác nhận nhận được tiền."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Reconciliation));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectQr(
        int paymentId,
        string reason,
        CancellationToken cancellationToken)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["ErrorMessage"] =
                "Vui lòng nhập lý do yêu cầu khách kiểm tra/gửi lại giao dịch.";
            return RedirectToAction(nameof(Reconciliation));
        }

        if (reason.Length > 500)
        {
            TempData["ErrorMessage"] = "Lý do tối đa 500 ký tự.";
            return RedirectToAction(nameof(Reconciliation));
        }

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.RejectQrPaymentAsync(
            paymentId,
            staffId,
            cancellationToken);

        if (result.Succeeded)
        {
            var payment = await _dbContext.Payments
                .AsNoTracking()
                .Include(item => item.Booking)
                .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

            if (payment is not null)
            {
                var notification = await _dbContext.Notifications
                    .Where(item =>
                        item.UserId == payment.Booking.CustomerId &&
                        item.Title == "Chưa xác nhận được chuyển khoản")
                    .OrderByDescending(item => item.NotificationId)
                    .FirstOrDefaultAsync(cancellationToken);

                if (notification is not null)
                {
                    notification.Message =
                        $"SmartCar chưa thể xác nhận giao dịch của đơn #{payment.BookingId}. " +
                        $"Lý do: {reason}. Vui lòng kiểm tra lại và gửi xác nhận lần nữa.";
                    notification.IsRead = false;
                    notification.ReadAt = null;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã từ chối giao dịch và gửi lý do cho khách."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Reconciliation));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CollectExtensionCash(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .Include(item => item.Extensions)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê.";
            return RedirectToStaffDetails(bookingId);
        }

        if (booking.Status != BookingStatus.Rented)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Chỉ thu phí gia hạn khi chuyến đang ở trạng thái Đang thuê.";
            return RedirectToStaffDetails(bookingId);
        }

        if (booking.Payments.Any(payment =>
                payment.Type == PaymentType.Extension &&
                payment.Status == PaymentStatus.AwaitingConfirmation))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Phí gia hạn đang có chuyển khoản/QR chờ Staff đối soát. Không được đồng thời thu tiền mặt.";
            return RedirectToStaffDetails(bookingId);
        }

        var approvedExtensions = booking.Extensions
            .Where(extension => extension.Status == BookingExtensionStatus.Approved)
            .OrderByDescending(extension => extension.RequestedAt)
            .ToList();

        if (approvedExtensions.Count != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = approvedExtensions.Count == 0
                ? "Không có yêu cầu gia hạn đã duyệt đang chờ thanh toán."
                : "Dữ liệu có nhiều yêu cầu gia hạn đã duyệt cùng lúc. Cần đối soát trước khi thu tiền.";
            return RedirectToStaffDetails(bookingId);
        }

        var extension = approvedExtensions[0];
        if (extension.AdditionalAmount <= 0m ||
            extension.RequestedReturnDate <= booking.ReturnDate)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Yêu cầu gia hạn có số tiền hoặc thời gian không hợp lệ. Chưa được thu tiền.";
            return RedirectToStaffDetails(bookingId);
        }

        var pendingPayments = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Extension &&
                payment.Status == PaymentStatus.Pending)
            .OrderBy(payment => payment.PaymentId)
            .ToList();

        var payment = pendingPayments.FirstOrDefault();
        if (payment is null)
        {
            payment = new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.Extension
            };
            booking.Payments.Add(payment);
        }

        var paidAt = DateTime.UtcNow;
        payment.Amount = extension.AdditionalAmount;
        payment.Method = PaymentMethods.Cash;
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = paidAt;
        payment.TransactionCode =
            $"CASH-EXT-{booking.BookingId}-{paidAt:yyyyMMddHHmmssfff}";

        foreach (var duplicate in pendingPayments.Where(item => item != payment))
        {
            duplicate.Status = PaymentStatus.Failed;
            duplicate.Method = PaymentMethods.NotSelected;
            duplicate.PaidAt = null;
            duplicate.TransactionCode = null;
        }

        booking.ReturnDate = extension.RequestedReturnDate;
        booking.NumberOfDays = Math.Max(
            1,
            (int)Math.Ceiling(
                (booking.ReturnDate - booking.PickupDate).TotalHours / 24d));
        booking.RentalAmount += extension.AdditionalAmount;
        booking.TotalAmount += extension.AdditionalAmount;
        extension.Status = BookingExtensionStatus.Paid;
        extension.PaidAt = paidAt;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã thanh toán gia hạn tại quầy",
            Message =
                $"Đơn #{booking.BookingId}: SmartCar đã ghi nhận {extension.AdditionalAmount:N0} đồng phí gia hạn bằng tiền mặt. Thời gian trả mới: {booking.ReturnDate:dd/MM/yyyy HH:mm}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            staffId,
            "StaffCollectExtensionCash",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Nhân viên thu {extension.AdditionalAmount:N0} đồng phí gia hạn bằng tiền mặt cho đơn #{booking.BookingId}; thời gian trả mới {booking.ReturnDate:dd/MM/yyyy HH:mm}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã thu {extension.AdditionalAmount:N0} đ phí gia hạn bằng tiền mặt và áp dụng thời gian trả mới.";
        return RedirectToStaffDetails(bookingId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CollectOverdueCompensationDebtCash(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê.";
            return RedirectToStaffDetails(bookingId);
        }

        if (booking.Status is not (BookingStatus.Rented or BookingStatus.PendingInspection))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Chỉ được thu bồi thường quá hạn khi chuyến đang thuê hoặc đang kiểm tra xe trả.";
            return RedirectToStaffDetails(bookingId);
        }

        var debts = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.OverdueCompensationDebt &&
                payment.Amount > 0m)
            .OrderBy(payment => payment.PaymentId)
            .ToList();

        if (debts.Any(payment => payment.Status == PaymentStatus.AwaitingConfirmation))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Khoản bồi thường quá hạn đang có chuyển khoản/QR chờ Staff đối soát. " +
                "Không được đồng thời thu tiền mặt.";
            return booking.Status == BookingStatus.PendingInspection
                ? RedirectToAction("Inspect", "Returns", new { bookingId })
                : RedirectToStaffDetails(bookingId);
        }

        var pendingDebts = debts
            .Where(payment => payment.Status == PaymentStatus.Pending)
            .ToList();

        if (pendingDebts.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Đơn không còn khoản bồi thường quá hạn cần thu bằng tiền mặt.";
            return booking.Status == BookingStatus.PendingInspection
                ? RedirectToAction("Inspect", "Returns", new { bookingId })
                : RedirectToStaffDetails(bookingId);
        }

        var debtRelations = pendingDebts
            .Select(payment => new
            {
                Payment = payment,
                Parsed = OverdueCompensationLedger.TryParseDebtRelation(
                    payment.TransactionCode,
                    out var renterBookingId,
                    out var affectedBookingId),
                RenterBookingId = renterBookingId,
                AffectedBookingId = affectedBookingId
            })
            .ToList();

        if (debtRelations.Any(item =>
                !item.Parsed ||
                item.RenterBookingId != booking.BookingId ||
                item.AffectedBookingId == booking.BookingId))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Có khoản bồi thường quá hạn không còn mã liên kết hợp lệ giữa đơn gây ảnh hưởng và đơn bị ảnh hưởng. Chưa thu tiền.";
            return booking.Status == BookingStatus.PendingInspection
                ? RedirectToAction("Inspect", "Returns", new { bookingId })
                : RedirectToStaffDetails(bookingId);
        }

        var affectedBookingIds = debtRelations
            .Select(item => item.AffectedBookingId)
            .Distinct()
            .ToArray();

        var affectedBookings = await _dbContext.Bookings
            .Include(item => item.Payments)
            .Where(item => affectedBookingIds.Contains(item.BookingId))
            .ToDictionaryAsync(item => item.BookingId, cancellationToken);

        if (affectedBookings.Count != affectedBookingIds.Length)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Không tìm thấy đầy đủ đơn bị ảnh hưởng để mở khoản bồi thường tương ứng. Chưa thu tiền.";
            return booking.Status == BookingStatus.PendingInspection
                ? RedirectToAction("Inspect", "Returns", new { bookingId })
                : RedirectToStaffDetails(bookingId);
        }

        var paidAt = DateTime.UtcNow;
        var total = pendingDebts.Sum(payment => payment.Amount);

        foreach (var payment in pendingDebts)
        {
            payment.Method = PaymentMethods.Cash;
            payment.Status = PaymentStatus.Paid;
            payment.PaidAt = paidAt;

            // Giữ nguyên OVERDUE-DEBT-{đơn A}-{đơn B}-... để hồ sơ lịch sử
            // vẫn truy ngược được khoản tiền này phát sinh do ảnh hưởng đơn nào.
        }

        var releasedCompensationTotal = 0m;
        foreach (var relationGroup in debtRelations.GroupBy(item => item.AffectedBookingId))
        {
            var affectedBookingId = relationGroup.Key;
            var affectedBooking = affectedBookings[affectedBookingId];
            var deductionPrefix =
                OverdueCompensationLedger.BuildDepositDeductionRelationPrefix(
                    booking.BookingId,
                    affectedBookingId);
            var debtPrefix =
                OverdueCompensationLedger.BuildDebtRelationPrefix(
                    booking.BookingId,
                    affectedBookingId);

            var fundedFromDeposit = booking.Payments
                .Where(item =>
                    item.Type == PaymentType.AdditionalCharge &&
                    item.Method == PaymentMethods.DepositDeduction &&
                    item.Status == PaymentStatus.Paid &&
                    !string.IsNullOrWhiteSpace(item.TransactionCode) &&
                    item.TransactionCode.StartsWith(
                        deductionPrefix,
                        StringComparison.OrdinalIgnoreCase))
                .Sum(item => item.Amount);

            var fundedFromDebt = booking.Payments
                .Where(item =>
                    item.Type == PaymentType.OverdueCompensationDebt &&
                    item.Status == PaymentStatus.Paid &&
                    !string.IsNullOrWhiteSpace(item.TransactionCode) &&
                    item.TransactionCode.StartsWith(
                        debtPrefix,
                        StringComparison.OrdinalIgnoreCase))
                .Sum(item => item.Amount);

            var compensationRefundAlreadyRecorded = affectedBooking.Payments
                .Where(item =>
                    item.Type == PaymentType.Refund &&
                    item.Method == PaymentMethods.CompensationRefund &&
                    BookingWorkflowRules.CountsTowardRefundTotal(item.Status) &&
                    OverdueCompensationLedger.CountsTowardRefundRelation(
                        item.TransactionCode,
                        booking.BookingId,
                        affectedBookingId))
                .Sum(item => item.Amount);

            var releaseAmount = OverdueCompensationLedger.CalculateRefundReleaseAmount(
                fundedFromDeposit,
                fundedFromDebt,
                compensationRefundAlreadyRecorded);

            if (releaseAmount <= 0m)
            {
                continue;
            }

            affectedBooking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = releaseAmount,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.AwaitingRefund,
                TransactionCode = OverdueCompensationLedger.BuildFundedRefundCode(
                    booking.BookingId,
                    affectedBookingId,
                    paidAt)
            });
            affectedBooking.RefundAmount = affectedBooking.Payments
                .Where(item =>
                    item.Type == PaymentType.Refund &&
                    BookingWorkflowRules.CountsTowardRefundTotal(item.Status))
                .Sum(item => item.Amount);
            affectedBooking.RefundReason = AppendText(
                affectedBooking.RefundReason,
                $"Đã thực thu thêm {releaseAmount:N0} đồng bồi thường từ đơn #{booking.BookingId}; " +
                "khoản tương ứng đã chuyển sang chờ Admin duyệt hoàn.");
            releasedCompensationTotal += releaseAmount;

            _dbContext.Notifications.Add(new Notification
            {
                UserId = affectedBooking.CustomerId,
                Title = "Bồi thường đã có thêm nguồn thanh toán",
                Message =
                    $"Đơn #{affectedBooking.BookingId}: SmartCar đã thực thu thêm {releaseAmount:N0} đồng " +
                    $"từ khách của đơn #{booking.BookingId}. Khoản này đã chuyển sang quy trình chờ Admin duyệt hoàn tiền."
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã thanh toán bồi thường quá hạn tại quầy",
            Message =
                $"Đơn #{booking.BookingId}: SmartCar đã ghi nhận {total:N0} đồng " +
                "bồi thường quá hạn còn thiếu bằng tiền mặt."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            staffId,
            "StaffCollectOverdueCompensationDebtCash",
            nameof(Payment),
            bookingId.ToString(),
            $"Nhân viên thu {total:N0} đồng bồi thường quá hạn còn thiếu bằng tiền mặt " +
            $"cho đơn #{bookingId}; số khoản: {pendingDebts.Count}; " +
            $"mở thêm {releasedCompensationTotal:N0} đồng bồi thường có nguồn cho đơn bị ảnh hưởng.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã ghi nhận {total:N0} đ bồi thường quá hạn bằng tiền mặt.";

        return booking.Status == BookingStatus.PendingInspection
            ? RedirectToAction("Inspect", "Returns", new { bookingId })
            : RedirectToStaffDetails(bookingId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCounterQr(
        int bookingId,
        bool transferConfirmed,
        CancellationToken cancellationToken)
    {
        if (!transferConfirmed)
        {
            TempData["ErrorMessage"] =
                "Chỉ ghi nhận chuyển khoản sau khi khách đã quét QR/thực hiện chuyển tiền tại quầy.";
            return RedirectToStaffDetails(bookingId);
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new
            {
                item.BookingId,
                item.CustomerId,
                item.Status
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê.";
            return RedirectToAction("Bookings", "Staff");
        }

        if (booking.Status != BookingStatus.PendingPayment)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đã được Admin duyệt và đang Chờ thanh toán mới được báo chuyển khoản tại quầy.";
            return RedirectToStaffDetails(bookingId);
        }

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.SubmitQrPaymentAsync(
            bookingId,
            booking.CustomerId,
            PaymentType.Rental,
            staffId,
            cancellationToken);

        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToStaffDetails(bookingId);
        }

        TempData["SuccessMessage"] =
            $"Đã ghi nhận khách báo chuyển khoản. Staff có tối đa {RentalPolicy.BookingTransferReconciliationHoldMinutes} phút để đối soát; giao dịch chưa được coi là đã thanh toán.";
        return RedirectToStaffDetails(bookingId);
    }

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";

    private IActionResult RedirectToStaffDetails(int bookingId) =>
        RedirectToAction("Details", "Staff", new { id = bookingId });
}
