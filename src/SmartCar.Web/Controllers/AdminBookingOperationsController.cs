using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBookingOperationsController : Controller
{
    private readonly IBookingOperationService _operationService;

    public AdminBookingOperationsController(IBookingOperationService operationService)
    {
        _operationService = operationService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        CancelBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = ModelState.IsValid
            ? await _operationService.CancelByAdminAsync(
                adminId,
                new CancelBookingRequest(model.BookingId, model.Reason),
                cancellationToken)
            : RefundResult.Failure("Vui lòng nhập lý do hủy đơn.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? $"Đã hủy đơn và hoàn {result.RefundAmount:N0} đồng cho khách."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNoShow(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.MarkNoShowAsync(
            bookingId,
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận khách không đến nhận xe."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }
}
