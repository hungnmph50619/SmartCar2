using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.VehicleDocuments;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class VehicleDocumentsController : Controller
{
    private readonly IVehicleDocumentService _documentService;
    private readonly IVehicleService _vehicleService;

    public VehicleDocumentsController(
        IVehicleDocumentService documentService,
        IVehicleService vehicleService)
    {
        _documentService = documentService;
        _vehicleService = vehicleService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        int? vehicleId,
        CancellationToken cancellationToken)
    {
        ViewBag.VehicleId = vehicleId;
        var documents = vehicleId.HasValue
            ? await _documentService.GetByVehicleAsync(vehicleId.Value, cancellationToken)
            : await _documentService.GetAllAsync(cancellationToken);

        return View(documents);
    }

    [HttpGet]
    public async Task<IActionResult> Create(
        int? vehicleId,
        CancellationToken cancellationToken)
    {
        await LoadVehiclesAsync(vehicleId, cancellationToken);
        return View(new VehicleDocumentViewModel
        {
            VehicleId = vehicleId ?? 0
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        VehicleDocumentViewModel form,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadVehiclesAsync(form.VehicleId, cancellationToken);
            return View(form);
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _documentService.CreateAsync(
            new SaveVehicleDocumentRequest(
                form.VehicleId,
                form.DocumentType,
                form.DocumentNumber,
                form.IssuedDate,
                form.ExpiryDate,
                form.ImagePath,
                form.Notes),
            adminId,
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

        TempData["SuccessMessage"] = "Đã thêm giấy tờ xe.";
        return RedirectToAction(nameof(Index), new { vehicleId = form.VehicleId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        int id,
        int? vehicleId,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _documentService.DeleteAsync(id, adminId, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xóa giấy tờ xe."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index), new { vehicleId });
    }

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
