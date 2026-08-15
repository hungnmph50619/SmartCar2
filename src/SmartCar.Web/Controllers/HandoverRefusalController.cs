using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class HandoverRefusalController : Controller
{
    private readonly IHandoverRefusalService _handoverRefusalService;

    public HandoverRefusalController(
        IHandoverRefusalService handoverRefusalService)
    {
        _handoverRefusalService = handoverRefusalService;
    }

    [HttpGet]
    public async Task<IActionResult> Confirm(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var preview = await _handoverRefusalService.GetPreviewAsync(
            bookingId,
            cancellationToken);

        if (preview is null)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang Sẵn sàng giao xe và chưa bàn giao mới được hủy theo chính sách khách từ chối ký biên bản.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View(preview);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _handoverRefusalService.CancelAsync(
            adminId,
            bookingId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] =
            result.Succeeded
                ? $"Đã hủy đơn do khách từ chối ký biên bản bàn giao. Tổng khoản hoàn cần xử lý: {result.RefundAmount:N0} đồng."
                : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }
}
