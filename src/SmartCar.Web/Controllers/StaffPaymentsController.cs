using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Payments;
using SmartCar.Application.Features.Audits;
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
                item.Status,
                item.PolicyJson
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

        var bookingPolicy = RentalPolicySnapshot.FromJson(booking.PolicyJson);
        TempData["SuccessMessage"] =
            $"Đã ghi nhận khách báo chuyển khoản. Staff có tối đa {bookingPolicy.BookingTransferReconciliationHoldMinutes} phút để đối soát; giao dịch chưa được coi là đã thanh toán.";
        return RedirectToStaffDetails(bookingId);
    }

    private IActionResult RedirectToStaffDetails(int bookingId) =>
        RedirectToAction("Details", "Staff", new { id = bookingId });
}
