using System.Data;
using System.Globalization;
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
    private const string ForceMajeureMarker = "[FORCE_MAJEURE]";
    private const string LegacyDepositDeductionPrefix = "EXT-COMP-";

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
        _paymentService =
            paymentService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        PaymentStatus? status,
        PaymentType? type,
        string? section,
        CancellationToken cancellationToken)
    {
        ViewBag.Status =
            status;

        ViewBag.Type =
            type;

        var payments =
            await _paymentService
                .GetAdminPaymentsAsync(
                    status,
                    type,
                    cancellationToken);

        return View(
            payments);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmQr(
        int paymentId,
        int? returnBookingId,
        CancellationToken cancellationToken)
    {
        var adminId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier)
            ?? string.Empty;

        var result =
            await _paymentService
                .ConfirmQrPaymentAsync(
                    paymentId,
                    adminId,
                    cancellationToken);

        TempData[
            result.Succeeded
                ? "SuccessMessage"
                : "ErrorMessage"] =
            result.Succeeded

                ? "Đã xác nhận nhận được tiền."
                : string.Join("; ", result.Errors);

                : string.Join(
                    "; ",
                    result.Errors);

        return RedirectAfterPaymentAction(
            returnBookingId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectQr(
        int paymentId,
        int? returnBookingId,
        CancellationToken cancellationToken)
    {
        var adminId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier)
            ?? string.Empty;

        var result =
            await _paymentService
                .RejectQrPaymentAsync(
                    paymentId,
                    adminId,
                    cancellationToken);

        TempData[
            result.Succeeded
                ? "SuccessMessage"
                : "ErrorMessage"] =
            result.Succeeded

                ? "Đã trả giao dịch về trạng thái chờ thanh toán."

                : string.Join(
                    "; ",
                    result.Errors);

        return RedirectAfterPaymentAction(
            returnBookingId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmRefund(
        int paymentId,
        string transactionCode,
        CancellationToken cancellationToken)
    {
        var adminId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier)
            ?? string.Empty;

        var result =
            await _paymentService
                .ConfirmRefundAsync(
                    paymentId,
                    adminId,
                    transactionCode,
                    cancellationToken);

        TempData[
            result.Succeeded
                ? "SuccessMessage"
                : "ErrorMessage"] =
            result.Succeeded

                ? "Đã xác nhận hoàn tiền cho khách."

                : string.Join(
                    "; ",
                    result.Errors);

        return RedirectToAction(
            nameof(Index));
    }

    private IActionResult RedirectAfterPaymentAction(
        int? returnBookingId)
    {
        if (returnBookingId.HasValue &&
            returnBookingId.Value > 0)
        {
            return RedirectToAction(
                "Details",
                "AdminBookings",
                new
                {
                    id = returnBookingId.Value
                });
        }

        return RedirectToAction(
            nameof(Index));
    }
}
