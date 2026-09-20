using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Extensions;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminExtensionCompensationsController : Controller
{
    private const string ForceMajeureMarker = "[FORCE_MAJEURE]";
    private const string ActualCompensationPrefix = "EXT-ACTUAL-COMP-";
    private const string DeniedExtensionCompensationPrefix = "EXT-DENIED-COMP-";

    private static readonly BookingStatus[] BlockingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly IExtensionService _extensionService;
    private readonly IBookingOperationService _bookingOperationService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;

    public AdminExtensionCompensationsController(
        IExtensionService extensionService,
        IBookingOperationService bookingOperationService,
        IAuditService auditService,
        ApplicationDbContext dbContext)
    {
        _extensionService = extensionService;
        _bookingOperationService = bookingOperationService;
        _auditService = auditService;
        _dbContext = dbContext;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelConflictingBooking(
        int extensionId,
        bool customerContacted,
        decimal compensationAmount,
        string? compensationReason,
        CancellationToken cancellationToken)
    {
        if (!customerContacted)
        {
            TempData["ErrorMessage"] =
                "Vui lòng xác nhận đã liên hệ khách B và khách không chấp nhận phương án đổi xe.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        if (compensationAmount < 0)
        {
            TempData["ErrorMessage"] = "Khoản bồi thường không được nhỏ hơn 0 đồng.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        compensationReason = compensationReason?.Trim();
        if (compensationAmount > 0 && string.IsNullOrWhiteSpace(compensationReason))
        {
            TempData["ErrorMessage"] =
                "Vui lòng ghi căn cứ thiệt hại thực tế trước khi nhập khoản bồi thường.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        if (compensationReason?.Length > 250)
        {
            TempData["ErrorMessage"] = "Căn cứ bồi thường tối đa 250 ký tự.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .FirstOrDefaultAsync(
                item => item.BookingExtensionId == extensionId,
                cancellationToken);

        if (extension is null ||
            extension.Status != BookingExtensionStatus.Pending ||
            extension.Booking.Status != BookingStatus.Rented ||
            !IsForceMajeure(extension.CustomerNote))
        {
            TempData["ErrorMessage"] =
                "Yêu cầu gia hạn không còn hợp lệ để xử lý xung đột bất khả kháng.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var conflict = await FindConflictingBookingAsync(extension, cancellationToken);
        if (conflict is null)
        {
            TempData["ErrorMessage"] = "Đơn B không còn xung đột. Hãy tải lại trang.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var availableDeposit = CalculateAvailableDeposit(extension.Booking.Payments);
        if (compensationAmount > availableDeposit)
        {
            TempData["ErrorMessage"] =
                $"Cọc A còn khả dụng {availableDeposit:N0} đ. " +
                "Khoản bồi thường trong trường hợp bất khả kháng chỉ được lấy từ cọc A, nên không thể nhập cao hơn số cọc còn lại. " +
                "Hệ thống không tự lấy tiền SmartCar để bù phần vượt.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var affectedBookingId = conflict.BookingId;

        var cancelResult = await _bookingOperationService.CancelByAdminAsync(
            adminId,
            new CancelBookingRequest(
                affectedBookingId,
                $"SmartCar phải hủy do đơn #{extension.BookingId} phát sinh gia hạn bất khả kháng có minh chứng và khách B không chấp nhận phương án đổi xe."),
            cancellationToken);

        if (!cancelResult.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", cancelResult.Errors);
            return RedirectToAction("Index", "AdminExtensions");
        }

        extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .FirstAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        var cancelledBooking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstAsync(item => item.BookingId == affectedBookingId, cancellationToken);

        if (compensationAmount > 0)
        {
            var deductionCode =
                $"{ActualCompensationPrefix}{extension.BookingId}-{extension.BookingExtensionId}-{affectedBookingId}-{DateTime.UtcNow:yyyyMMddHHmmss}";

            extension.Booking.Payments.Add(new Payment
            {
                Type = PaymentType.AdditionalCharge,
                Amount = compensationAmount,
                Method = PaymentMethods.DepositDeduction,
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow,
                TransactionCode = deductionCode
            });

            cancelledBooking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = compensationAmount,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.AwaitingRefund
            });

            cancelledBooking.RefundAmount = cancelledBooking.Payments
                .Where(payment =>
                    payment.Type == PaymentType.Refund &&
                    BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
                .Sum(payment => payment.Amount);
            cancelledBooking.RefundReason = AppendText(
                cancelledBooking.RefundReason,
                $"Bồi thường thiệt hại thực tế {compensationAmount:N0} đồng do đơn #{extension.BookingId} ảnh hưởng. " +
                $"Căn cứ: {compensationReason}.");

            extension.AdminNote = AppendText(
                extension.AdminNote,
                $"Đã xử lý đơn kế tiếp #{affectedBookingId}. " +
                $"Bồi thường thiệt hại thực tế {compensationAmount:N0} đồng được khấu trừ từ tiền cọc của bạn; " +
                "đây không phải khoản bạn phải chuyển thêm. " +
                $"Căn cứ: {compensationReason}.");

            _dbContext.Notifications.Add(new Notification
            {
                UserId = extension.Booking.CustomerId,
                Title = "Đối soát tiền cọc khi gia hạn",
                Message =
                    $"Đơn #{extension.BookingId}: do đơn kế tiếp #{affectedBookingId} phải hủy, " +
                    $"SmartCar ghi nhận bồi thường thiệt hại thực tế {compensationAmount:N0} đồng và khấu trừ khoản này từ cọc đã nộp. " +
                    "Bạn không cần chuyển thêm khoản bồi thường này. Phí gia hạn được thanh toán riêng sau khi yêu cầu được duyệt."
            });

            _dbContext.Notifications.Add(new Notification
            {
                UserId = cancelledBooking.CustomerId,
                Title = "Hoàn tiền và bồi thường",
                Message =
                    $"Đơn #{affectedBookingId} đã hủy. Ngoài các khoản đã thanh toán được hoàn theo chính sách, " +
                    $"SmartCar ghi nhận thêm {compensationAmount:N0} đồng bồi thường thiệt hại thực tế đang chờ chuyển."
            });
        }
        else
        {
            extension.AdminNote = AppendText(
                extension.AdminNote,
                $"Đã xử lý đơn kế tiếp #{affectedBookingId}. Không ghi nhận khoản bồi thường thiệt hại thực tế; không khấu trừ cọc cho phần bồi thường.");

            _dbContext.Notifications.Add(new Notification
            {
                UserId = extension.Booking.CustomerId,
                Title = "Đã xử lý đơn thuê kế tiếp",
                Message =
                    $"Đơn #{extension.BookingId}: SmartCar đã xử lý xung đột với đơn #{affectedBookingId}. " +
                    "Hiện không ghi nhận khoản bồi thường thiệt hại thực tế từ tiền cọc."
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ResolveExtensionConflictByCancellation",
            nameof(Booking),
            affectedBookingId.ToString(),
            $"Hủy đơn #{affectedBookingId} do xung đột gia hạn bất khả kháng của đơn #{extension.BookingId}. " +
            $"Hoàn theo chính sách: {cancelResult.RefundAmount:N0} đồng. " +
            $"Bồi thường thực tế từ cọc A: {compensationAmount:N0} đồng. " +
            $"Căn cứ: {(string.IsNullOrWhiteSpace(compensationReason) ? "không phát sinh" : compensationReason)}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = compensationAmount > 0
            ? $"Đã hủy đơn B, tạo khoản hoàn và bồi thường {compensationAmount:N0} đ từ cọc A. Các khoản hoàn của B sẽ được gộp khi chuyển tiền."
            : "Đã hủy đơn B và tạo khoản hoàn. Không phát sinh bồi thường từ cọc A.";

        return RedirectToAction("Index", "AdminExtensions");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RecordIntentionalConflictCompensation(
        int renterBookingId,
        int affectedBookingId,
        bool violationConfirmed)
    {
        TempData["ErrorMessage"] =
            "Luồng xử lý cũ đã được vô hiệu hóa để tránh hai công thức bồi thường khác nhau. " +
            "Hãy xử lý vi phạm quá hạn tại chi tiết đơn đang thuê; hệ thống sẽ trừ cọc khả dụng, " +
            "ghi phần còn thiếu thành nợ và chỉ cho chuyển bồi thường sau khi nguồn tiền đã đủ.";

        return RedirectToAction(
            "Details",
            "AdminBookings",
            new { id = renterBookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(
        int id,
        string? adminNote,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _extensionService.ApproveAsync(
            id,
            adminId,
            false,
            adminNote,
            cancellationToken);

        if (result.Succeeded)
        {
            var extension = await _dbContext.BookingExtensions
                .Include(item => item.Booking)
                    .ThenInclude(booking => booking.Payments)
                .FirstOrDefaultAsync(item => item.BookingExtensionId == id, cancellationToken);

            if (extension is not null)
            {
                var prefix =
                    $"{ActualCompensationPrefix}{extension.BookingId}-{extension.BookingExtensionId}-";
                var compensationDeducted = extension.Booking.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.AdditionalCharge &&
                        payment.Method == PaymentMethods.DepositDeduction &&
                        payment.Status == PaymentStatus.Paid &&
                        !string.IsNullOrWhiteSpace(payment.TransactionCode) &&
                        payment.TransactionCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Sum(payment => payment.Amount);

                if (compensationDeducted > 0)
                {
                    extension.AdminNote = AppendText(
                        extension.AdminNote,
                        $"Khoản bồi thường {compensationDeducted:N0} đồng đã được khấu trừ từ cọc trước khi duyệt; " +
                        $"khách chỉ cần thanh toán phí gia hạn {extension.AdditionalAmount:N0} đồng để thời gian trả mới có hiệu lực.");

                    _dbContext.Notifications.Add(new Notification
                    {
                        UserId = extension.Booking.CustomerId,
                        Title = "Gia hạn đã duyệt - thông tin thanh toán",
                        Message =
                            $"Đơn #{extension.BookingId}: phí gia hạn cần chuyển là {extension.AdditionalAmount:N0} đồng. " +
                            $"Khoản bồi thường {compensationDeducted:N0} đồng đã được khấu trừ từ cọc trước đó, không chuyển thêm lần nữa."
                    });

                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã duyệt gia hạn. Ngày trả mới chỉ có hiệu lực sau khi xác nhận tiền gia hạn."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Index", "AdminExtensions");
    }

    private async Task<Booking?> FindConflictingBookingAsync(
        BookingExtension extension,
        CancellationToken cancellationToken)
    {
        var storeBoundary = extension.RequestedReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.StorePickup));
        var deliveryBoundary = extension.RequestedReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.Delivery));

        return await _dbContext.Bookings
            .Include(item => item.Payments)
            .Where(other =>
                other.VehicleId == extension.Booking.VehicleId &&
                other.BookingId != extension.BookingId &&
                BlockingStatuses.Contains(other.Status) &&
                other.ReturnDate > extension.OriginalReturnDate &&
                (
                    other.PickupMethod == VehiclePickupMethod.Delivery
                        ? other.PickupDate < deliveryBoundary
                        : other.PickupDate < storeBoundary
                ))
            .OrderBy(other => other.PickupDate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static decimal CalculateAvailableDeposit(IEnumerable<Payment> payments)
    {
        var paid = payments
            .Where(payment => payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var refundedOrPlanned = payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);
        var deducted = payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method == PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        return Math.Max(0m, paid - refundedOrPlanned - deducted);
    }

    private static bool IsForceMajeure(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(ForceMajeureMarker, StringComparison.Ordinal);

    private static string AppendText(string? current, string addition)
    {
        var combined = string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";

        return combined.Length <= 500
            ? combined
            : combined[..500];
    }
}
