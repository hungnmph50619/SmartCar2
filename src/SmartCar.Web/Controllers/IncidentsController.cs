using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.Incidents;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class IncidentsController : Controller
{
    private readonly IIncidentService _incidentService;
    private readonly IVehicleService _vehicleService;

    public IncidentsController(
        IIncidentService incidentService,
        IVehicleService vehicleService)
    {
        _incidentService = incidentService;
        _vehicleService = vehicleService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        IncidentStatus? status,
        CancellationToken cancellationToken)
    {
        ViewBag.Status = status;
        return View(await _incidentService.GetAllAsync(status, cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Create(
        int? vehicleId,
        int? bookingId,
        CancellationToken cancellationToken)
    {
        await LoadVehiclesAsync(vehicleId, cancellationToken);
        return View(new IncidentCreateViewModel
        {
            VehicleId = vehicleId ?? 0,
            BookingId = bookingId,
            OccurredAt = DateTime.Now
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        IncidentCreateViewModel form,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadVehiclesAsync(form.VehicleId, cancellationToken);
            return View(form);
        }

        var result = await _incidentService.CreateAsync(
            new CreateIncidentRequest(
                form.VehicleId,
                form.BookingId,
                form.IncidentType,
                form.OccurredAt,
                form.Location,
                form.Description,
                form.EstimatedCost,
                form.FineAmount,
                form.CustomerLiabilityAmount,
                form.EvidencePaths,
                form.Notes),
            GetUserId(),
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            await LoadVehiclesAsync(form.VehicleId, cancellationToken);
            return View(form);
        }

        TempData["SuccessMessage"] = "Đã ghi nhận sự cố hoặc vi phạm.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartInvestigation(
        int id,
        CancellationToken cancellationToken)
    {
        var result = await _incidentService.StartInvestigationAsync(
            id,
            GetUserId(),
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã chuyển sự cố sang trạng thái đang xử lý."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(
        IncidentResolveViewModel form,
        CancellationToken cancellationToken)
    {
        var result = ModelState.IsValid
            ? await _incidentService.ResolveAsync(
                new ResolveIncidentRequest(
                    form.IncidentId,
                    form.ActualCost,
                    form.FineAmount,
                    form.CustomerLiabilityAmount,
                    form.Notes,
                    form.RequiresMaintenance),
                GetUserId(),
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Dữ liệu xử lý sự cố không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã hoàn tất xử lý sự cố."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    private string GetUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private async Task LoadVehiclesAsync(
        int? selectedId,
        CancellationToken cancellationToken)
    {
        ViewBag.Vehicles = new SelectList(
            await _vehicleService.GetAllAsync(cancellationToken),
            "VehicleId",
            "VehicleName",
            selectedId);
    }
}
