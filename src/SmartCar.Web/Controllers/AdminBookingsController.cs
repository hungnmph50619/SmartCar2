using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBookingsController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;

    public AdminBookingsController(
        IBookingService bookingService,
        IAuditService auditService)
    {
        _bookingService = bookingService;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        BookingStatus? status,
        CancellationToken cancellationToken)
    {
        ViewBag.Status = status;
        return View(await _bookingService.GetAdminBookingsAsync(status, cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(id, cancellationToken);
        return booking is null ? NotFound() : View(booking);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id, CancellationToken cancellationToken)
    {
        var result = await _bookingService.ConfirmAsync(id, cancellationToken);
        SetMessage(result, "Đã xác nhận đơn thuê và tạo khoản thanh toán.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "Confirm",
                id,
                $"Xác nhận đơn thuê #{id} và chuyển sang chờ thanh toán.",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        RejectBookingViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do từ chối.";
            return RedirectToAction(nameof(Details), new { id = model.BookingId });
        }

        var result = await _bookingService.RejectAsync(
            model.BookingId,
            model.Reason,
            cancellationToken);
        SetMessage(result, "Đã từ chối đơn thuê.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "Reject",
                model.BookingId,
                $"Từ chối đơn thuê #{model.BookingId}. Lý do: {model.Reason.Trim()}",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(int id, CancellationToken cancellationToken)
    {
        var result = await _bookingService.MarkReadyForPickupAsync(id, cancellationToken);
        SetMessage(result, "Đã đánh dấu xe sẵn sàng bàn giao.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "MarkReady",
                id,
                $"Đánh dấu xe của đơn #{id} đã sẵn sàng bàn giao.",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private void SetMessage(OperationResult result, string successMessage)
    {
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? successMessage
            : string.Join("; ", result.Errors);
    }

    private Task WriteAuditAsync(
        string action,
        int bookingId,
        string description,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        return _auditService.WriteAsync(
            adminId,
            action,
            nameof(Booking),
            bookingId.ToString(),
            description,
            ipAddress: ipAddress,
            cancellationToken: cancellationToken);
    }
}
