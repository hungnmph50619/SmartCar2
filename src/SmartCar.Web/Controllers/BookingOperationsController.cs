using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class BookingOperationsController : Controller
{
    private readonly IBookingOperationService _operationService;

    public BookingOperationsController(IBookingOperationService operationService)
    {
        _operationService = operationService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        CancelBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do hủy đơn.";
            return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
        }

        var result = await _operationService.CancelByCustomerAsync(
            customerId,
            new CancelBookingRequest(model.BookingId, model.Reason),
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? result.RefundAmount > 0m
                ? $"Đã hủy đơn. Hệ thống đã ghi nhận {result.RefundAmount:N0} đồng vào quy trình chờ Admin duyệt hoàn; đây chưa phải trạng thái đã chuyển tiền."
                : "Đã hủy đơn. Không phát sinh khoản hoàn mới."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
    }
}
