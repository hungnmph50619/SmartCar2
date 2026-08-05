using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.Brands;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminVehiclesController : Controller
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly IVehicleService _vehicleService;
    private readonly IBrandService _brandService;
    private readonly IWebHostEnvironment _environment;

    public AdminVehiclesController(
        IVehicleService vehicleService,
        IBrandService brandService,
        IWebHostEnvironment environment)
    {
        _vehicleService = vehicleService;
        _brandService = brandService;
        _environment = environment;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _vehicleService.GetAllAsync(cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        await LoadBrandsAsync(null, cancellationToken);
        return View(new VehicleFormViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        VehicleFormViewModel model,
        CancellationToken cancellationToken)
    {
        ValidateImages(model.Images);
        if (!ModelState.IsValid)
        {
            await LoadBrandsAsync(model.BrandId, cancellationToken);
            return View(model);
        }

        var result = await _vehicleService.CreateAsync(
            new CreateVehicleRequest(
                model.BrandId,
                model.VehicleName,
                model.Model,
                model.LicensePlate,
                model.ManufactureYear,
                model.Seats,
                model.Transmission,
                model.FuelType,
                model.Color,
                model.DailyPrice,
                model.CurrentMileage,
                model.Description),
            cancellationToken);

        if (!result.Succeeded || !result.VehicleId.HasValue)
        {
            AddErrors(result.Errors);
            await LoadBrandsAsync(model.BrandId, cancellationToken);
            return View(model);
        }

        await SaveImagesAsync(result.VehicleId.Value, model.Images, cancellationToken);
        TempData["SuccessMessage"] = "Đã thêm xe mới.";
        return RedirectToAction(nameof(Edit), new { id = result.VehicleId.Value });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleService.GetByIdAsync(id, cancellationToken);
        if (vehicle is null)
        {
            return NotFound();
        }

        await LoadBrandsAsync(vehicle.BrandId, cancellationToken);
        ViewBag.Vehicle = vehicle;

        return View(new VehicleFormViewModel
        {
            VehicleId = vehicle.VehicleId,
            BrandId = vehicle.BrandId,
            VehicleName = vehicle.VehicleName,
            Model = vehicle.Model,
            LicensePlate = vehicle.LicensePlate,
            ManufactureYear = vehicle.ManufactureYear,
            Seats = vehicle.Seats,
            Transmission = vehicle.Transmission,
            FuelType = vehicle.FuelType,
            Color = vehicle.Color,
            DailyPrice = vehicle.DailyPrice,
            CurrentMileage = vehicle.CurrentMileage,
            Description = vehicle.Description,
            RowVersionBase64 = Convert.ToBase64String(vehicle.RowVersion)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        VehicleFormViewModel model,
        CancellationToken cancellationToken)
    {
        ValidateImages(model.Images);
        if (!ModelState.IsValid)
        {
            await PrepareEditViewAsync(model, cancellationToken);
            return View(model);
        }

        byte[] rowVersion;
        try
        {
            rowVersion = Convert.FromBase64String(model.RowVersionBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            ModelState.AddModelError(string.Empty, "Phiên bản dữ liệu xe không hợp lệ.");
            await PrepareEditViewAsync(model, cancellationToken);
            return View(model);
        }

        var result = await _vehicleService.UpdateAsync(
            new UpdateVehicleRequest(
                model.VehicleId,
                model.BrandId,
                model.VehicleName,
                model.Model,
                model.LicensePlate,
                model.ManufactureYear,
                model.Seats,
                model.Transmission,
                model.FuelType,
                model.Color,
                model.DailyPrice,
                model.CurrentMileage,
                model.Description,
                rowVersion),
            cancellationToken);

        if (!result.Succeeded)
        {
            AddErrors(result.Errors);
            await PrepareEditViewAsync(model, cancellationToken);
            return View(model);
        }

        await SaveImagesAsync(model.VehicleId, model.Images, cancellationToken);
        TempData["SuccessMessage"] = "Đã cập nhật xe.";
        return RedirectToAction(nameof(Edit), new { id = model.VehicleId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStatus(
        int id,
        VehicleStatus status,
        CancellationToken cancellationToken)
    {
        var result = await _vehicleService.ChangeStatusAsync(id, status, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã cập nhật trạng thái xe."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrimaryImage(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken)
    {
        var result = await _vehicleService.SetPrimaryImageAsync(
            vehicleId,
            imageId,
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã chọn ảnh đại diện."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Edit), new { id = vehicleId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteImage(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleService.GetByIdAsync(vehicleId, cancellationToken);
        var imagePath = vehicle?.Images
            .FirstOrDefault(image => image.VehicleImageId == imageId)?.ImagePath;

        var result = await _vehicleService.DeleteImageAsync(
            vehicleId,
            imageId,
            cancellationToken);

        if (result.Succeeded && !string.IsNullOrWhiteSpace(imagePath))
        {
            var fullPath = Path.Combine(
                _environment.WebRootPath,
                imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xóa ảnh xe."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Edit), new { id = vehicleId });
    }

    private async Task SaveImagesAsync(
        int vehicleId,
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        if (images.Count == 0)
        {
            return;
        }

        var relativeFolder = $"uploads/vehicles/{vehicleId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var vehicle = await _vehicleService.GetByIdAsync(vehicleId, cancellationToken);
        var firstImageIsPrimary = vehicle?.Images.Count == 0;

        foreach (var image in images.Where(file => file.Length > 0))
        {
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(folder, fileName);

            await using var stream = System.IO.File.Create(fullPath);
            await image.CopyToAsync(stream, cancellationToken);

            var relativePath = $"/{relativeFolder}/{fileName}";
            await _vehicleService.AddImageAsync(
                vehicleId,
                relativePath,
                firstImageIsPrimary,
                cancellationToken);
            firstImageIsPrimary = false;
        }
    }

    private void ValidateImages(IEnumerable<IFormFile> images)
    {
        foreach (var image in images.Where(file => file.Length > 0))
        {
            var extension = Path.GetExtension(image.FileName);
            if (!AllowedExtensions.Contains(extension))
            {
                ModelState.AddModelError(nameof(VehicleFormViewModel.Images),
                    "Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.");
            }

            if (image.Length > 5 * 1024 * 1024)
            {
                ModelState.AddModelError(nameof(VehicleFormViewModel.Images),
                    "Mỗi ảnh không được vượt quá 5 MB.");
            }
        }
    }

    private async Task PrepareEditViewAsync(
        VehicleFormViewModel model,
        CancellationToken cancellationToken)
    {
        await LoadBrandsAsync(model.BrandId, cancellationToken);
        ViewBag.Vehicle = await _vehicleService.GetByIdAsync(model.VehicleId, cancellationToken);
    }

    private async Task LoadBrandsAsync(int? selectedId, CancellationToken cancellationToken)
    {
        ViewBag.Brands = new SelectList(
            await _brandService.GetActiveAsync(cancellationToken),
            "BrandId",
            "BrandName",
            selectedId);
    }

    private void AddErrors(IEnumerable<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }
}
