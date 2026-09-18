using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffPaymentsController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPaymentService _paymentService;

    public StaffPaymentsController(
        ApplicationDbContext dbContext,
        IPaymentService paymentService)
    {
        _dbContext = dbContext;
        _paymentService = paymentService;
    }

    [HttpGet]
    public async Task<IActionResult> Reconciliation(
        CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetAdminPaymentsAsync(
            PaymentStatus.AwaitingConfirmation,
            null,
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

    private IActionResult RedirectToStaffDetails(int bookingId) =>
        RedirectToAction("Details", "Staff", new { id = bookingId });
}
