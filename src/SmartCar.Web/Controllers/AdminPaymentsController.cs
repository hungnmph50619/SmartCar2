using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Extensions;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminPaymentsController : Controller
{
    private const string CompensationMarker = "[NEXT_BOOKING_COMPENSATION]";

    private readonly IPaymentService _paymentService;
    private readonly IExtensionService _extensionService;
    private readonly ApplicationDbContext _dbContext;

    public AdminPaymentsController(
        IPaymentService paymentService,
        IExtensionService extensionService,
        ApplicationDbContext dbContext)
    {
        _paymentService = paymentService;
        _extensionService = extensionService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        PaymentStatus? status,
        PaymentType? type,
        string? section,
        CancellationToken cancellationToken)
    {
        var reviewAll =
            string.IsNullOrWhiteSpace(section) &&
            !type.HasValue &&
            status == PaymentStatus.AwaitingConfirmation;

        section = reviewAll ? "review" : ResolveSection(section, type);

        ViewBag.Status = status;
        ViewBag.Type = type;
        ViewBag.Section = section;

        var payments = await _paymentService.GetAdminPaymentsAsync(
            status,
            type,
            cancellationToken);

        var filtered = payments.Where(payment => section switch
        {
            "review" =>
                payment.Status == PaymentStatus.AwaitingConfirmation &&
                payment.Type != PaymentType.Refund,
            "adjustment" =>
                payment.Type is
                    PaymentType.Extension or
                    PaymentType.AdditionalCharge or
                    PaymentType.VehicleSwapAdjustment,
            "refund" => payment.Type == PaymentType.Refund,
            _ => payment.Type is PaymentType.Rental or PaymentType.Deposit
        }).ToList();

        return View(filtered);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmQr(
        int paymentId,
        string? section,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.ConfirmQrPaymentAsync(
            paymentId,
            adminId,
            cancellationToken);

        if (result.Succeeded)
        {
            var payment = await _dbContext.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

            if (payment?.Type == PaymentType.Extension)
            {
                var extensionResult = await _extensionService.MarkPaidAsync(
                    payment.BookingId,
                    cancellationToken);

                if (!extensionResult.Succeeded)
                {
                    TempData["ErrorMessage"] =
                        "Đã xác nhận tiền gia hạn nhưng chưa cập nhật được ngày trả mới: " +
                        string.Join("; ", extensionResult.Errors);
                    return RedirectToAction(nameof(Index), new { section = section ?? "adjustment" });
                }
            }
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xác nhận nhận được tiền."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index), new { section = section ?? "collection" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectQr(
        int paymentId,
        string? section,
        string reason,
        CancellationToken cancellationToken)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do yêu cầu khách gửi lại xác nhận thanh toán.";
            return RedirectToAction(nameof(Index), new { section = section ?? "collection" });
        }

        if (reason.Length > 500)
        {
            TempData["ErrorMessage"] = "Lý do tối đa 500 ký tự.";
            return RedirectToAction(nameof(Index), new { section = section ?? "collection" });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.RejectQrPaymentAsync(
            paymentId,
            adminId,
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
            ? "Đã gửi lý do để khách kiểm tra và gửi lại."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index), new { section = section ?? "collection" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmRefund(
        int paymentId,
        string transactionCode,
        string? section,
        CancellationToken cancellationToken)
    {
        var compensationDeduction = await ApplyExtensionCompensationDeductionAsync(
            paymentId,
            cancellationToken);

        if (!compensationDeduction.Succeeded)
        {
            TempData["ErrorMessage"] = compensationDeduction.Error;
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.ConfirmRefundAsync(
            paymentId,
            adminId,
            transactionCode,
            cancellationToken);

        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        var payment = await _dbContext.Payments
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

        var hasOtherPendingRefund = payment?.Booking.Payments.Any(item =>
            item.Type == PaymentType.Refund &&
            item.Status == PaymentStatus.AwaitingRefund) == true;

        var completedAfterRefund = payment is not null &&
            payment.Type == PaymentType.Refund &&
            payment.Status == PaymentStatus.Refunded &&
            payment.Booking.Status == BookingStatus.AwaitingRefund &&
            !hasOtherPendingRefund;

        if (completedAfterRefund)
        {
            payment!.Booking.Status = BookingStatus.Completed;

            _dbContext.Notifications.Add(new Notification
            {
                UserId = payment.Booking.CustomerId,
                Title = "Đơn thuê đã hoàn tất",
                Message = compensationDeduction.DeductedAmount > 0
                    ? $"Đơn #{payment.BookingId} đã hoàn tất. Đã khấu trừ {compensationDeduction.DeductedAmount:N0} đồng theo phương án bồi thường và hoàn phần cọc còn lại."
                    : $"Đơn #{payment.BookingId} đã hoàn tất. Các khoản hoàn đã được xử lý."
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        TempData["SuccessMessage"] = completedAfterRefund
            ? "Đã xử lý khoản hoàn cuối cùng. Chuyến thuê đã hoàn tất."
            : hasOtherPendingRefund
                ? "Đã xác nhận khoản hoàn này. Vẫn còn khoản hoàn khác cần xử lý."
                : "Đã xác nhận hoàn tiền.";

        if (completedAfterRefund && payment is not null)
        {
            return RedirectToAction("Details", "AdminTripRecords", new { id = payment.BookingId });
        }

        return RedirectToAction(nameof(Index), new { section = section ?? "refund" });
    }

    private async Task<(bool Succeeded, decimal DeductedAmount, string? Error)> ApplyExtensionCompensationDeductionAsync(
        int paymentId,
        CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Extensions)
            .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

        if (payment is null)
        {
            return (false, 0m, "Không tìm thấy khoản hoàn tiền.");
        }

        var isDepositRefund =
            payment.Method == PaymentMethods.DepositRefund ||
            (payment.Method == PaymentMethods.BankTransferRefund && payment.Booking.VehicleReturn is not null);

        if (payment.Type != PaymentType.Refund ||
            payment.Status != PaymentStatus.AwaitingRefund ||
            payment.Booking.Status != BookingStatus.AwaitingRefund ||
            !isDepositRefund)
        {
            return (true, 0m, null);
        }

        var alreadyApplied = payment.Booking.Payments.Any(item =>
            item.Type == PaymentType.AdditionalCharge &&
            item.Status == PaymentStatus.Paid &&
            item.Method == PaymentMethods.DepositDeduction &&
            !string.IsNullOrWhiteSpace(item.TransactionCode) &&
            item.TransactionCode.StartsWith("EXT-COMP-", StringComparison.Ordinal));

        if (alreadyApplied)
        {
            return (true, 0m, null);
        }

        var compensationAmount = payment.Booking.Extensions
            .Sum(extension => ExtractCompensationAmount(extension.CustomerNote));

        if (compensationAmount <= 0)
        {
            return (true, 0m, null);
        }

        var deduction = Math.Min(payment.Amount, compensationAmount);
        if (deduction <= 0)
        {
            return (true, 0m, null);
        }

        payment.Amount -= deduction;
        payment.Booking.RefundAmount = Math.Max(0m, payment.Booking.RefundAmount - deduction);
        payment.Booking.AdditionalAmount += deduction;
        payment.Booking.TotalAmount += deduction;
        payment.Booking.RefundReason = AppendText(
            payment.Booking.RefundReason,
            $"Khấu trừ {deduction:N0} đồng từ cọc để bồi thường đơn thuê kế tiếp.");

        payment.Booking.Payments.Add(new Payment
        {
            Type = PaymentType.AdditionalCharge,
            Amount = deduction,
            Method = PaymentMethods.DepositDeduction,
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow,
            TransactionCode = $"EXT-COMP-{payment.BookingId}-{DateTime.UtcNow:yyyyMMddHHmmss}"
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "Đối soát tiền cọc",
            Message = $"Đơn #{payment.BookingId}: khấu trừ {deduction:N0} đồng theo phương án bồi thường. Cọc còn hoàn: {payment.Amount:N0} đồng."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return (true, deduction, null);
    }

    private static decimal ExtractCompensationAmount(string? customerNote)
    {
        if (string.IsNullOrWhiteSpace(customerNote))
        {
            return 0m;
        }

        var total = 0m;
        var searchIndex = 0;
        while (searchIndex < customerNote.Length)
        {
            var markerIndex = customerNote.IndexOf(
                CompensationMarker,
                searchIndex,
                StringComparison.Ordinal);

            if (markerIndex < 0)
            {
                break;
            }

            var valueStart = markerIndex + CompensationMarker.Length;
            var valueEnd = customerNote.IndexOfAny(new[] { '|', '\r', '\n' }, valueStart);
            var rawValue = valueEnd < 0
                ? customerNote[valueStart..]
                : customerNote[valueStart..valueEnd];

            if (decimal.TryParse(
                    rawValue.Trim(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var parsed) &&
                parsed > 0)
            {
                total += parsed;
            }

            searchIndex = valueEnd < 0 ? customerNote.Length : valueEnd + 1;
        }

        return total;
    }

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";

    private static string ResolveSection(string? section, PaymentType? type)
    {
        if (section is "collection" or "adjustment" or "refund" or "review")
        {
            return section;
        }

        return type switch
        {
            PaymentType.Extension or
            PaymentType.AdditionalCharge or
            PaymentType.VehicleSwapAdjustment => "adjustment",
            PaymentType.Refund => "refund",
            _ => "collection"
        };
    }
}
