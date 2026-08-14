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
        bool contactAttempted,
        bool arrivedAtPickupLocation,
        string contactNote,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.MarkNoShowAsync(
            new MarkNoShowRequest(
                bookingId,
                contactAttempted,
                arrivedAtPickupLocation,
                contactNote ?? string.Empty),
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận khách không đến nhận xe sau khi hoàn tất bước xác minh liên hệ. Đơn kết thúc và không hoàn tiền."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }
}
