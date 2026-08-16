using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly ApplicationDbContext _dbContext;

    public AdminPaymentsController(
        IPaymentService paymentService,
        ApplicationDbContext dbContext)
    {
        _paymentService = paymentService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        PaymentStatus? status,
        PaymentType? type,
        CancellationToken cancellationToken)
    {
        ViewBag.Status = status;
        ViewBag.Type = type;

        var payments =
            await _paymentService.GetAdminPaymentsAsync(
                status,
                type,
                cancellationToken);

        return View(payments);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmQr(
        int paymentId,
        CancellationToken cancellationToken)
    {
        var adminId =
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? string.Empty;

        var result =
            await _paymentService.ConfirmQrPaymentAsync(
                paymentId,
                adminId,
                cancellationToken);

        TempData[result.Succeeded
            ? "SuccessMessage"
            : "ErrorMessage"] = result.Succeeded
                ? "Đã xác nhận nhận được tiền."
                : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectQr(
        int paymentId,
        CancellationToken cancellationToken)
    {
        var adminId =
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? string.Empty;

        var result =
            await _paymentService.RejectQrPaymentAsync(
                paymentId,
                adminId,
                cancellationToken);

        TempData[result.Succeeded
            ? "SuccessMessage"
            : "ErrorMessage"] = result.Succeeded
                ? "Đã trả giao dịch về trạng thái chờ thanh toán."
                : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmRefund(
        int paymentId,
        string transactionCode,
        CancellationToken cancellationToken)
    {
        var adminId =
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? string.Empty;

        var result =
            await _paymentService.ConfirmRefundAsync(
                paymentId,
                adminId,
                transactionCode,
                cancellationToken);

        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToAction(nameof(Index));
        }

        var payment = await _dbContext.Payments
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(
                item => item.PaymentId == paymentId,
                cancellationToken);

        var completedAfterRefund =
            payment is not null &&
            payment.Type == PaymentType.Refund &&
            payment.Status == PaymentStatus.Refunded &&
            payment.Booking.Status == BookingStatus.AwaitingRefund;

        if (completedAfterRefund)
        {
            payment!.Booking.Status = BookingStatus.Completed;

            _dbContext.Notifications.Add(
                new Notification
                {
                    UserId = payment.Booking.CustomerId,
                    Title = "Đơn thuê đã hoàn tất",
                    Message =
                        $"Đơn #{payment.BookingId} đã hoàn tất sau khi SmartCar xác nhận hoàn " +
                        $"{payment.Amount:N0} đồng tiền cọc."
                });

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        TempData["SuccessMessage"] = completedAfterRefund
            ? "Đã xác nhận hoàn cọc. Chuyến thuê đã hoàn tất."
            : "Đã xác nhận hoàn tiền cho khách.";

        return RedirectToAction(nameof(Index));
    }
}
