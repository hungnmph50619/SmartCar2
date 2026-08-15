using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.VehicleDocuments;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class VehicleDocumentsController : Controller
{
    private const long MaximumDocumentImageBytes = 5 * 1024 * 1024;

    private readonly IVehicleDocumentService _documentService;
    private readonly IVehicleService _vehicleService;
    private readonly IWebHostEnvironment _environment;

    public VehicleDocumentsController(
        IVehicleDocumentService documentService,
        IVehicleService vehicleService,
        IWebHostEnvironment environment)
    {
        _documentService = documentService;
        _vehicleService = vehicleService;
        _environment = environment;
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
        if (form.ImageFile is not null)
        {
            var imageError = await ImageFileValidator.ValidateAsync(
                form.ImageFile,
                MaximumDocumentImageBytes,
                cancellationToken);

            if (imageError is not null)
            {
                ModelState.AddModelError(nameof(form.ImageFile), imageError);
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadVehiclesAsync(form.VehicleId, cancellationToken);
            return View(form);
        }

        var imagePath = await SaveDocumentImageAsync(form.ImageFile, cancellationToken);
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _documentService.CreateAsync(
            new SaveVehicleDocumentRequest(
                form.VehicleId,
                form.DocumentType,
                form.DocumentNumber,
                form.IssuedDate,
                form.ExpiryDate,
                imagePath,
                form.Notes),
            adminId,
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteDocumentImageIfExists(imagePath);

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

    private void DeleteDocumentImageIfExists(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        var fullPath = Path.Combine(
            _environment.WebRootPath,
            imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }
    }

    private async Task<string?> SaveDocumentImageAsync(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return null;
        }

        const string relativeFolder = "uploads/documents";
        var folder = Path.Combine(_environment.WebRootPath, "uploads", "documents");
        Directory.CreateDirectory(folder);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(folder, fileName);

        await using var stream = System.IO.File.Create(fullPath);
        await file.CopyToAsync(stream, cancellationToken);

        return $"/{relativeFolder}/{fileName}";
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
