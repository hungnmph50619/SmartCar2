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

            cancelledBooking.RefundAmount += compensationAmount;
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
    public async Task<IActionResult> RecordIntentionalConflictCompensation(
        int renterBookingId,
        int affectedBookingId,
        bool violationConfirmed,
        CancellationToken cancellationToken)
    {
        if (!violationConfirmed)
        {
            TempData["ErrorMessage"] =
                "Chỉ ghi nhận khi đã xác nhận khách A được thông báo từ chối gia hạn nhưng vẫn cố tình không trả xe đúng hạn và làm ảnh hưởng đơn B.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        if (renterBookingId <= 0 || affectedBookingId <= 0 || renterBookingId == affectedBookingId)
        {
            TempData["ErrorMessage"] = "Mã đơn A/B không hợp lệ.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var renterBooking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == renterBookingId, cancellationToken);

        var affectedBooking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == affectedBookingId, cancellationToken);

        if (renterBooking is null || affectedBooking is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn A hoặc đơn B.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        if (renterBooking.VehicleId != affectedBooking.VehicleId)
        {
            TempData["ErrorMessage"] = "Đơn A và đơn B không sử dụng cùng xe nên không đủ căn cứ ghi nhận xung đột này.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var renterEffectiveReturn = renterBooking.VehicleReturn?.ReturnedAt ?? DateTime.Now;
        var renterStillHoldingVehicle = renterBooking.Status == BookingStatus.Rented;
        if (!renterStillHoldingVehicle && renterEffectiveReturn <= affectedBooking.PickupDate)
        {
            TempData["ErrorMessage"] =
                "Thời điểm trả xe của A không làm chậm thời điểm nhận xe của B; chưa đủ căn cứ áp dụng bồi thường do cố tình không trả đúng hạn.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        if (renterStillHoldingVehicle && DateTime.Now <= affectedBooking.PickupDate)
        {
            TempData["ErrorMessage"] =
                "Đơn B chưa đến giờ nhận xe nên chưa thể xác nhận A đã làm ảnh hưởng việc giao xe cho B.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var contractCompensation = Math.Max(0m, affectedBooking.TotalAmount);
        if (contractCompensation <= 0)
        {
            TempData["ErrorMessage"] = "Giá trị hợp đồng của đơn B không hợp lệ.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var availableDeposit = CalculateAvailableDeposit(renterBooking.Payments);
        if (availableDeposit < contractCompensation)
        {
            TempData["ErrorMessage"] =
                $"Theo luồng đã thống nhất, A phải bồi thường bằng giá hợp đồng B: {contractCompensation:N0} đ, " +
                $"nhưng cọc A hiện chỉ còn {availableDeposit:N0} đ. " +
                "Hệ thống không tự lấy tiền SmartCar để bù phần thiếu; cần xử lý thu bổ sung từ A trước khi xác nhận bồi thường đủ cho B.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var duplicate = renterBooking.Payments.Any(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Method == PaymentMethods.DepositDeduction &&
            payment.Status == PaymentStatus.Paid &&
            !string.IsNullOrWhiteSpace(payment.TransactionCode) &&
            payment.TransactionCode.StartsWith(
                $"{DeniedExtensionCompensationPrefix}{renterBookingId}-{affectedBookingId}-",
                StringComparison.OrdinalIgnoreCase));

        if (duplicate)
        {
            TempData["ErrorMessage"] = "Khoản bồi thường cho cặp đơn A/B này đã được ghi nhận trước đó.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        if (BlockingStatuses.Contains(affectedBooking.Status))
        {
            var cancelResult = await _bookingOperationService.CancelByAdminAsync(
                adminId,
                new CancelBookingRequest(
                    affectedBookingId,
                    $"SmartCar phải hủy do khách của đơn #{renterBookingId} đã bị từ chối gia hạn nhưng vẫn không trả xe đúng hạn, làm ảnh hưởng đơn #{affectedBookingId}."),
                cancellationToken);

            if (!cancelResult.Succeeded)
            {
                TempData["ErrorMessage"] = string.Join("; ", cancelResult.Errors);
                return RedirectToAction("Index", "AdminExtensions");
            }

            affectedBooking = await _dbContext.Bookings
                .Include(item => item.Payments)
                .FirstAsync(item => item.BookingId == affectedBookingId, cancellationToken);
        }
        else if (affectedBooking.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected))
        {
            TempData["ErrorMessage"] =
                "Trạng thái đơn B không phù hợp để tạo khoản bồi thường do hủy/không thể giao xe.";
            return RedirectToAction("Index", "AdminExtensions");
        }

        renterBooking.Payments.Add(new Payment
        {
            Type = PaymentType.AdditionalCharge,
            Amount = contractCompensation,
            Method = PaymentMethods.DepositDeduction,
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow,
            TransactionCode =
                $"{DeniedExtensionCompensationPrefix}{renterBookingId}-{affectedBookingId}-{DateTime.UtcNow:yyyyMMddHHmmss}"
        });

        affectedBooking.Payments.Add(new Payment
        {
            Type = PaymentType.Refund,
            Amount = contractCompensation,
            Method = PaymentMethods.CompensationRefund,
            Status = PaymentStatus.AwaitingRefund
        });
        affectedBooking.RefundAmount += contractCompensation;
        affectedBooking.RefundReason = AppendText(
            affectedBooking.RefundReason,
            $"Bồi thường {contractCompensation:N0} đồng bằng giá hợp đồng do đơn #{renterBookingId} cố tình không trả xe đúng hạn làm ảnh hưởng.");

        _dbContext.Notifications.Add(new Notification
        {
            UserId = renterBooking.CustomerId,
            Title = "Khấu trừ cọc do không trả xe đúng hạn",
            Message =
                $"Đơn #{renterBookingId}: do đã được thông báo từ chối gia hạn nhưng vẫn không trả xe đúng hạn làm ảnh hưởng đơn #{affectedBookingId}, " +
                $"SmartCar ghi nhận bồi thường {contractCompensation:N0} đồng bằng giá hợp đồng của đơn bị ảnh hưởng và khấu trừ từ cọc đã nộp."
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = affectedBooking.CustomerId,
            Title = "Hoàn tiền và bồi thường do không thể giao xe",
            Message =
                $"Đơn #{affectedBookingId}: SmartCar ghi nhận thêm {contractCompensation:N0} đồng bồi thường do đơn trước không trả xe đúng hạn. " +
                "Khoản này sẽ được gộp cùng các khoản hoàn đang chờ của đơn."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ResolveExtensionConflictByCancellation",
            nameof(Booking),
            affectedBookingId.ToString(),
            $"TH1: đơn #{renterBookingId} đã bị từ chối gia hạn nhưng vẫn không trả đúng hạn, ảnh hưởng đơn #{affectedBookingId}. " +
            $"Bồi thường bằng giá hợp đồng B: {contractCompensation:N0} đồng, khấu trừ từ cọc A.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã ghi nhận bồi thường {contractCompensation:N0} đ bằng giá hợp đồng B và khấu trừ từ cọc A. Khoản này được gộp vào lần hoàn tiền cho B.";

        return RedirectToAction("Index", "AdminExtensions");
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
        return await _dbContext.Bookings
            .Include(item => item.Payments)
            .Where(other =>
                other.VehicleId == extension.Booking.VehicleId &&
                other.BookingId != extension.BookingId &&
                BlockingStatuses.Contains(other.Status) &&
                other.PickupDate < extension.RequestedReturnDate &&
                other.ReturnDate > extension.OriginalReturnDate)
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
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded)
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
