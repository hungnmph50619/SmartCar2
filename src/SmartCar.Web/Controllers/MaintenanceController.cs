using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.Maintenance;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class MaintenanceController : Controller
{
    private readonly IMaintenanceService _maintenanceService;
    private readonly IVehicleService _vehicleService;

    public MaintenanceController(
        IMaintenanceService maintenanceService,
        IVehicleService vehicleService)
    {
        _maintenanceService = maintenanceService;
        _vehicleService = vehicleService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        await LoadVehiclesAsync(cancellationToken);
        ViewBag.Form = new MaintenanceFormViewModel();
        return View(await _maintenanceService.GetAllAsync(cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        MaintenanceFormViewModel model,
        CancellationToken cancellationToken)
    {
        var result = ModelState.IsValid
            ? await _maintenanceService.CreateAsync(
                new CreateMaintenanceRequest(
                    model.VehicleId,
                    model.StartDate,
                    model.Content,
                    model.Cost,
                    model.ServiceProvider,
                    model.Mileage),
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Thông tin bảo trì không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã tạo phiếu bảo trì và khóa xe khỏi danh sách cho thuê."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(
        CompleteMaintenanceViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await _maintenanceService.CompleteAsync(
            model.MaintenanceId,
            model.FinalCost,
            model.CompletionNote,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã hoàn tất bảo trì và mở lại xe."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        int maintenanceId,
        string reason,
        CancellationToken cancellationToken)
    {
        var result = await _maintenanceService.CancelAsync(
            maintenanceId,
            reason,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã hủy phiếu bảo trì."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadVehiclesAsync(CancellationToken cancellationToken)
    {
        ViewBag.Vehicles = new SelectList(
            await _vehicleService.GetAllAsync(cancellationToken),
            "VehicleId",
            "VehicleName");
    }
}
