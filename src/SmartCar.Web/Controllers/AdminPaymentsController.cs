using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
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
    private readonly IPaymentService _paymentService;
    private readonly IExtensionService _extensionService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;

    public AdminPaymentsController(
        IPaymentService paymentService,
        IExtensionService extensionService,
        IAuditService auditService,
        ApplicationDbContext dbContext)
    {
        _paymentService = paymentService;
        _extensionService = extensionService;
        _auditService = auditService;
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
    public async Task<IActionResult> ConfirmRefundBatch(
        int bookingId,
        string? transactionCode,
        string? section,
        CancellationToken cancellationToken)
    {
        transactionCode = transactionCode?.Trim();
        if (string.IsNullOrWhiteSpace(transactionCode))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập mã giao dịch của lần chuyển hoàn tiền.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        if (transactionCode.Length > 100)
        {
            TempData["ErrorMessage"] = "Mã giao dịch hoàn tiền tối đa 100 ký tự.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê cần hoàn tiền.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        var pendingRefunds = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Status == PaymentStatus.AwaitingRefund)
            .OrderBy(item => item.PaymentId)
            .ToList();

        if (pendingRefunds.Count == 0)
        {
            TempData["ErrorMessage"] = "Đơn này không còn khoản hoàn tiền nào đang chờ xử lý.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        if (pendingRefunds.Any(item => item.Amount <= 0))
        {
            TempData["ErrorMessage"] = "Có khoản hoàn tiền không hợp lệ. Vui lòng kiểm tra lại dữ liệu trước khi xác nhận.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        var refundedAt = DateTime.UtcNow;
        var totalRefund = pendingRefunds.Sum(item => item.Amount);
        var breakdown = pendingRefunds
            .GroupBy(item => RefundPurposeText(item.Method))
            .Select(group => $"{group.Key}: {group.Sum(item => item.Amount):N0} đồng")
            .ToArray();

        foreach (var refund in pendingRefunds)
        {
            refund.Status = PaymentStatus.Refunded;
            refund.PaidAt = refundedAt;
            refund.TransactionCode = transactionCode;
        }

        var completedAfterRefund = booking.Status == BookingStatus.AwaitingRefund;
        if (completedAfterRefund)
        {
            booking.Status = BookingStatus.Completed;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Hoàn tiền thành công",
            Message =
                $"Đơn #{booking.BookingId}: SmartCar đã hoàn tổng {totalRefund:N0} đồng " +
                $"({string.Join("; ", breakdown)}). Mã giao dịch: {transactionCode}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        await _auditService.WriteAsync(
            adminId,
            "RefundBatch",
            nameof(Payment),
            booking.BookingId.ToString(),
            $"Hoàn tiền theo đơn #{booking.BookingId}: tổng {totalRefund:N0} đồng ({string.Join("; ", breakdown)}). Mã giao dịch: {transactionCode}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = completedAfterRefund
            ? $"Đã hoàn tổng {totalRefund:N0} đ. Chuyến thuê đã hoàn tất."
            : $"Đã hoàn tổng {totalRefund:N0} đ cho đơn #{booking.BookingId}.";

        if (completedAfterRefund)
        {
            return RedirectToAction("Details", "AdminTripRecords", new { id = booking.BookingId });
        }

        return RedirectToAction(nameof(Index), new { section = section ?? "refund" });
    }

    private static string RefundPurposeText(string? method) => method switch
    {
        PaymentMethods.DepositRefund => "Hoàn cọc",
        PaymentMethods.VehicleSwapRefund => "Hoàn chênh lệch đổi xe",
        PaymentMethods.CompensationRefund => "Hỗ trợ/bồi thường",
        PaymentMethods.BankTransferRefund => "Hoàn tiền thuê/phí giao",
        _ => "Hoàn tiền"
    };

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
