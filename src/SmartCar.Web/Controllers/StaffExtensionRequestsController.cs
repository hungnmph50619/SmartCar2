using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffExtensionRequestsController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IExtensionService _extensionService;
    private readonly IAuditService _auditService;

    public StaffExtensionRequestsController(
        IBookingService bookingService,
        IExtensionService extensionService,
        IAuditService auditService)
    {
        _bookingService = bookingService;
        _extensionService = extensionService;
        _auditService = auditService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateForCustomer(
        int bookingId,
        DateTime requestedReturnDate,
        string? note,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        var booking = await _bookingService.GetAdminBookingAsync(
            bookingId,
            cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang thuê mới được nhân viên ghi nhận yêu cầu gia hạn.";
            return RedirectToAction(
                "Details",
                "Staff",
                new { id = bookingId });
        }

        note = note?.Trim();
        if (string.IsNullOrWhiteSpace(note))
        {
            TempData["ErrorMessage"] =
                "Vui lòng ghi lý do khách yêu cầu gia hạn.";
            return RedirectToAction(
                "Details",
                "Staff",
                new { id = bookingId });
        }

        if (note.Length > 500)
        {
            TempData["ErrorMessage"] = "Lý do gia hạn tối đa 500 ký tự.";
            return RedirectToAction(
                "Details",
                "Staff",
                new { id = bookingId });
        }

        if (requestedReturnDate <= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Thời gian trả mới phải sau thời gian trả hiện tại.";
            return RedirectToAction(
                "Details",
                "Staff",
                new { id = bookingId });
        }

        var result = await _extensionService.RequestAsync(
            booking.CustomerId,
            new RequestExtensionRequest(
                bookingId,
                requestedReturnDate,
                $"Nhân viên ghi nhận yêu cầu qua điện thoại. {note}",
                IsForceMajeure: false,
                EvidenceNote: null),
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] =
            result.Succeeded
                ? "Đã ghi nhận yêu cầu gia hạn thông thường cho khách và chuyển sang bước Admin duyệt."
                : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            await _auditService.WriteAsync(
                staffId,
                "StaffCreateExtensionRequestForCustomer",
                nameof(BookingExtension),
                bookingId.ToString(),
                $"Nhân viên ghi nhận yêu cầu gia hạn thông thường qua điện thoại cho đơn #{bookingId} đến {requestedReturnDate:dd/MM/yyyy HH:mm}. Lý do: {note}",
                ipAddress:
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);
        }

        return RedirectToAction(
            "Details",
            "Staff",
            new { id = bookingId });
    }
}
