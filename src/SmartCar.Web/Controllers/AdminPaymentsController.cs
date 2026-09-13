using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
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
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;

    public AdminPaymentsController(
        IPaymentService paymentService,
        IAuditService auditService,
        ApplicationDbContext dbContext)
    {
        _paymentService = paymentService;
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
        // GET chỉ đọc dữ liệu. Mọi data-fix/migration tài chính phải chạy bằng một thao tác
        // quản trị riêng, không được âm thầm sửa ledger chỉ vì Admin mở trang.
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
                payment.Status != PaymentStatus.Failed &&
                payment.Type is
                    PaymentType.Extension or
                    PaymentType.AdditionalCharge or
                    PaymentType.VehicleSwapAdjustment,
            "refund" =>
                payment.Type == PaymentType.Refund &&
                payment.Status != PaymentStatus.Failed,
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

        // PaymentService là owner duy nhất của việc áp dụng thanh toán gia hạn.
        // Không gọi ExtensionService.MarkPaidAsync lần hai ở Controller.
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xác nhận nhận được tiền và áp dụng đúng nghiệp vụ liên quan."
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
    public async Task<IActionResult> ApproveRefundBatch(
        int bookingId,
        CancellationToken cancellationToken)
    {
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

        var awaitingApproval = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Status == PaymentStatus.AwaitingRefund)
            .OrderBy(item => item.PaymentId)
            .ToList();

        if (awaitingApproval.Count == 0)
        {
            TempData["ErrorMessage"] =
                "Đơn này không còn khoản hoàn tiền nào đang chờ chủ/Admin duyệt.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        if (awaitingApproval.Any(item => item.Amount <= 0))
        {
            TempData["ErrorMessage"] = "Có khoản hoàn tiền không hợp lệ. Vui lòng kiểm tra dữ liệu.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        foreach (var refund in awaitingApproval)
        {
            refund.Status = PaymentStatus.RefundApproved;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var totalRefund = awaitingApproval.Sum(item => item.Amount);
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        await _auditService.WriteAsync(
            adminId,
            "ApproveRefundBatch",
            nameof(Payment),
            booking.BookingId.ToString(),
            $"Chủ/Admin duyệt hoàn tiền cho đơn #{booking.BookingId}: tổng {totalRefund:N0} đồng. Chờ nhân viên thực hiện.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã duyệt hoàn {totalRefund:N0} đ cho đơn #{booking.BookingId}. Nhân viên sẽ thực hiện chuyển tiền.";

        return RedirectToAction(nameof(Index), new { section = "refund" });
    }

    // Route cũ được giữ để bookmark/form cũ không 404, nhưng không còn khả năng
    // đánh dấu đã hoàn. Admin chỉ duyệt; Staff mới được thực hiện chuyển tiền.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ConfirmRefundBatch(int bookingId)
    {
        TempData["ErrorMessage"] =
            "Admin chỉ có quyền duyệt khoản hoàn. Nhân viên phải thực hiện chuyển tiền và nhập mã giao dịch thực tế.";
        return RedirectToAction(nameof(Index), new { section = "refund" });
    }

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
