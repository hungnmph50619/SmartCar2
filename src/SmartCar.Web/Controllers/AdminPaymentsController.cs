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
        string? section,
        CancellationToken cancellationToken)
    {
        section = ResolveSection(section, type);
        ViewBag.Status = status;
        ViewBag.Type = type;
        ViewBag.Section = section;

        var payments = await _paymentService.GetAdminPaymentsAsync(
            status,
            type,
            cancellationToken);

        var filtered = payments.Where(payment => section switch
        {
            "adjustment" => payment.Type is PaymentType.Extension or PaymentType.AdditionalCharge,
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
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.RejectQrPaymentAsync(
            paymentId,
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã trả giao dịch về trạng thái chờ thanh toán."
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
            .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

        var completedAfterRefund = payment is not null &&
            payment.Type == PaymentType.Refund &&
            payment.Status == PaymentStatus.Refunded &&
            payment.Booking.Status == BookingStatus.AwaitingRefund;

        if (completedAfterRefund)
        {
            payment!.Booking.Status = BookingStatus.Completed;

            _dbContext.Notifications.Add(new Notification
            {
                UserId = payment.Booking.CustomerId,
                Title = "Đơn thuê đã hoàn tất",
                Message = $"Đơn #{payment.BookingId} đã hoàn tất. SmartCar đã hoàn {payment.Amount:N0} đồng."
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        TempData["SuccessMessage"] = completedAfterRefund
            ? "Đã hoàn cọc. Chuyến thuê đã hoàn tất."
            : "Đã xác nhận hoàn tiền.";

        if (completedAfterRefund && payment is not null)
        {
            return RedirectToAction("Details", "AdminTripRecords", new { id = payment.BookingId });
        }

        return RedirectToAction(nameof(Index), new { section = section ?? "refund" });
    }

    private static string ResolveSection(string? section, PaymentType? type)
    {
        if (section is "collection" or "adjustment" or "refund")
        {
            return section;
        }

        return type switch
        {
            PaymentType.Extension or PaymentType.AdditionalCharge => "adjustment",
            PaymentType.Refund => "refund",
            _ => "collection"
        };
    }
}
