using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffBookingOperationsController : Controller
{
    private readonly IBookingOperationService _operationService;

    public StaffBookingOperationsController(IBookingOperationService operationService)
    {
        _operationService = operationService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        CancelBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = ModelState.IsValid
            ? await _operationService.CancelByStaffAsync(
                staffId,
                new CancelBookingRequest(model.BookingId, model.Reason),
                cancellationToken)
            : RefundResult.Failure("Vui lòng nhập lý do hủy đơn.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? result.RefundAmount > 0
                ? $"Đã hủy đơn. Hệ thống đã tạo {result.RefundAmount:N0} đồng chờ Admin duyệt hoàn tiền."
                : "Đã hủy đơn và không phát sinh khoản hoàn mới."
            : string.Join("; ", result.Errors);

        return result.Succeeded
            ? RedirectToAction("Bookings", "Staff")
            : RedirectToAction("Details", "Staff", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNoShow(
        int bookingId,
        bool customerContacted,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.MarkNoShowAsync(
            bookingId,
            staffId,
            customerContacted,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận khách không đến. Các khoản hoàn phát sinh đang chờ Admin duyệt trước khi nhân viên chuyển tiền."
            : string.Join("; ", result.Errors);

        return result.Succeeded
            ? RedirectToAction("Bookings", "Staff")
            : RedirectToAction("Details", "Staff", new { id = bookingId });
    }
}
