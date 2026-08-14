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

    [HttpGet]
    public async Task<IActionResult> PreparePickup(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var preparation = await _operationService.GetPickupPreparationAsync(
            bookingId,
            cancellationToken);

        if (preparation is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn đã thanh toán để chuẩn bị giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View("PreparePickup", preparation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmPickupDeparture(
        int bookingId,
        bool customerConfirmed,
        string contactNote,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.ConfirmPickupDepartureAsync(
            new ConfirmPickupDepartureRequest(
                bookingId,
                customerConfirmed,
                contactNote ?? string.Empty),
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã lưu xác nhận của khách trước khi xuất phát. Đơn chuyển sang Sẵn sàng giao xe."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNoShow(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var preparation = await _operationService.GetNoShowPreparationAsync(
            bookingId,
            cancellationToken);

        if (preparation is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê để xử lý khách không đến nhận xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (DateTime.Now < preparation.PickupDate.AddMinutes(30))
        {
            TempData["ErrorMessage"] =
                "Chưa đủ 30 phút kể từ giờ nhận xe. Hãy tiếp tục liên hệ khách trước khi ghi nhận NoShow.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View("ConfirmNoShow", preparation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmNoShow(
        int bookingId,
        int contactAttemptCount,
        bool arrivedAtPickupLocation,
        string contactNote,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.MarkNoShowAsync(
            new MarkNoShowRequest(
                bookingId,
                contactAttemptCount,
                arrivedAtPickupLocation,
                contactNote ?? string.Empty),
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận NoShow sau khi xác minh đủ các bước. Hệ thống đã tính phí NoShow và tạo khoản hoàn tiền nếu còn số dư phải hoàn."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }
}
