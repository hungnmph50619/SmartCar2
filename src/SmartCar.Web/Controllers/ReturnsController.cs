using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Returns;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReturnsController : Controller
{
    private readonly IReturnService _returnService;

    public ReturnsController(IReturnService returnService)
    {
        _returnService = returnService;
    }

    [HttpGet]
    public IActionResult Create(int bookingId)
    {
        return View(new ReturnViewModel
        {
            BookingId = bookingId,
            ReturnedAt = DateTime.Now
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ReturnViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _returnService.CreateAsync(
            new CreateReturnRequest(
                model.BookingId,
                model.ReturnedAt,
                model.Mileage,
                model.FuelLevel,
                model.ExteriorCondition,
                model.InteriorCondition,
                model.HasDamage,
                model.ImagePaths,
                model.Notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            AddErrors(result.Errors);
            return View(model);
        }

        TempData["SuccessMessage"] = "Đã lập biên bản trả xe. Xe chuyển sang chờ kiểm tra.";
        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCharge(
        AddChargeViewModel model,
        CancellationToken cancellationToken)
    {
        var result = ModelState.IsValid
            ? await _returnService.AddChargeAsync(
                new AddChargeRequest(
                    model.BookingId,
                    model.ChargeType,
                    model.Description,
                    model.Amount),
                cancellationToken)
            : OperationResult.Failure("Thông tin phụ phí không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã thêm phụ phí và cập nhật tổng tiền."
            : string.Join("; ", result.Errors);
        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCharge(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken)
    {
        var result = await _returnService.RemoveChargeAsync(
            bookingId,
            additionalChargeId,
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xóa phụ phí."
            : string.Join("; ", result.Errors);
        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(
        CompleteBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await _returnService.CompleteAsync(
            model.BookingId,
            model.RequiresMaintenance,
            model.MaintenanceNote,
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã hoàn tất đơn và cập nhật trạng thái xe."
            : string.Join("; ", result.Errors);
        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    private void AddErrors(IEnumerable<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }
}
