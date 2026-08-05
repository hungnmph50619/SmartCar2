using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Manager)]
[Route("Dashboard/Vehicles")]
public class AdminVehiclesController : Controller
{
    private const long MaximumImageSize = 5_000_000;
    private const int MaximumImageCount = 12;

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;

    public AdminVehiclesController(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _environment = environment;
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var model = new AdminVehicleFormViewModel
        {
            ManufactureYear = DateTime.Today.Year,
            Seats = 5,
            Transmission = "Số tự động",
            FuelType = "Xăng",
            Status = VehicleStatus.Available
        };

        await PopulateFormDataAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(65_000_000)]
    public async Task<IActionResult> Create(
        AdminVehicleFormViewModel model,
        CancellationToken cancellationToken)
    {
        NormalizeModel(model);
        ValidateVehicleModel(model, isCreate: true);

        if (model.Status == VehicleStatus.Rented)
        {
            ModelState.AddModelError(nameof(model.Status), "Không thể tạo xe mới ở trạng thái đang được thuê.");
        }

        var licensePlateExists = await _dbContext.Vehicles
            .AnyAsync(vehicle => vehicle.LicensePlate == model.LicensePlate, cancellationToken);
        if (licensePlateExists)
        {
            ModelState.AddModelError(nameof(model.LicensePlate), "Biển số xe này đã tồn tại trong hệ thống.");
        }

        var brand = await ResolveBrandAsync(model, cancellationToken);
        if (!ModelState.IsValid || brand is null)
        {
            await PopulateFormDataAsync(model, cancellationToken);
            return View(model);
        }

        var vehicle = new Vehicle
        {
            BrandId = brand.BrandId,
            VehicleName = model.VehicleName,
            Model = model.Model,
            LicensePlate = model.LicensePlate,
            ManufactureYear = model.ManufactureYear,
            Seats = model.Seats,
            Transmission = model.Transmission,
            FuelType = model.FuelType,
            Color = model.Color,
            DailyPrice = model.DailyPrice,
            CurrentMileage = model.CurrentMileage,
            Status = model.Status,
            Description = model.Description,
            CreatedAt = DateTime.UtcNow
        };

        var createdFiles = new List<string>();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _dbContext.Vehicles.Add(vehicle);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var images = await SaveNewImagesAsync(
                vehicle.VehicleId,
                model.NewImages,
                startSortOrder: 0,
                makeFirstPrimary: true,
                createdFiles,
                cancellationToken);
            _dbContext.VehicleImages.AddRange(images);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            TempData["AdminSuccess"] = $"Đã thêm xe {vehicle.VehicleName} · {vehicle.LicensePlate}.";
            return RedirectToAction("Vehicles", "Dashboard");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DeleteFiles(createdFiles);
            ModelState.AddModelError(string.Empty, "Không thể thêm xe. Vui lòng kiểm tra dữ liệu và thử lại.");
            await PopulateFormDataAsync(model, cancellationToken);
            return View(model);
        }
    }

    [HttpGet("{id:int}/Edit")]
    public async Task<IActionResult> Edit(
        int id,
        CancellationToken cancellationToken)
    {
        var vehicle = await _dbContext.Vehicles
            .AsNoTracking()
            .Include(item => item.Images)
            .FirstOrDefaultAsync(item => item.VehicleId == id, cancellationToken);

        if (vehicle is null)
        {
            return NotFound();
        }

        var model = new AdminVehicleFormViewModel
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
            Status = vehicle.Status,
            Description = vehicle.Description,
            PrimaryImageId = vehicle.Images.FirstOrDefault(image => image.IsPrimary)?.VehicleImageId,
            ExistingImages = MapImages(vehicle.Images)
        };

        await PopulateFormDataAsync(model, cancellationToken, includeImages: false);
        return View(model);
    }

    [HttpPost("{id:int}/Edit")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(65_000_000)]
    public async Task<IActionResult> Edit(
        int id,
        AdminVehicleFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (id != model.VehicleId)
        {
            return BadRequest();
        }

        var vehicle = await _dbContext.Vehicles
            .Include(item => item.Images)
            .Include(item => item.Bookings)
            .FirstOrDefaultAsync(item => item.VehicleId == id, cancellationToken);

        if (vehicle is null)
        {
            return NotFound();
        }

        NormalizeModel(model);
        ValidateVehicleModel(model, isCreate: false, vehicle.Images.Count);

        var licensePlateExists = await _dbContext.Vehicles.AnyAsync(
            item => item.VehicleId != id && item.LicensePlate == model.LicensePlate,
            cancellationToken);
        if (licensePlateExists)
        {
            ModelState.AddModelError(nameof(model.LicensePlate), "Biển số xe này đã tồn tại trong hệ thống.");
        }

        var hasCurrentRental = vehicle.Bookings.Any(booking =>
            booking.Status is BookingStatus.Rented or BookingStatus.PendingInspection);
        if (hasCurrentRental && model.Status != VehicleStatus.Rented)
        {
            ModelState.AddModelError(nameof(model.Status), "Xe đang trong chuyến thuê nên phải giữ trạng thái đang được thuê.");
        }
        else if (!hasCurrentRental && model.Status == VehicleStatus.Rented)
        {
            ModelState.AddModelError(nameof(model.Status), "Trạng thái đang được thuê chỉ được hệ thống thiết lập khi bàn giao xe.");
        }

        var deleteIds = model.DeleteImageIds.Distinct().ToHashSet();
        var imagesToDelete = vehicle.Images
            .Where(image => deleteIds.Contains(image.VehicleImageId))
            .ToList();
        var remainingImageCount = vehicle.Images.Count - imagesToDelete.Count + model.NewImages.Count;
        if (remainingImageCount <= 0)
        {
            ModelState.AddModelError(nameof(model.NewImages), "Xe phải có ít nhất một ảnh.");
        }
        if (remainingImageCount > MaximumImageCount)
        {
            ModelState.AddModelError(nameof(model.NewImages), $"Mỗi xe chỉ được lưu tối đa {MaximumImageCount} ảnh.");
        }

        var brand = await ResolveBrandAsync(model, cancellationToken);
        if (!ModelState.IsValid || brand is null)
        {
            model.ExistingImages = MapImages(vehicle.Images);
            await PopulateFormDataAsync(model, cancellationToken, includeImages: false);
            return View(model);
        }

        vehicle.BrandId = brand.BrandId;
        vehicle.VehicleName = model.VehicleName;
        vehicle.Model = model.Model;
        vehicle.LicensePlate = model.LicensePlate;
        vehicle.ManufactureYear = model.ManufactureYear;
        vehicle.Seats = model.Seats;
        vehicle.Transmission = model.Transmission;
        vehicle.FuelType = model.FuelType;
        vehicle.Color = model.Color;
        vehicle.DailyPrice = model.DailyPrice;
        vehicle.CurrentMileage = model.CurrentMileage;
        vehicle.Status = model.Status;
        vehicle.Description = model.Description;

        var oldFilePaths = imagesToDelete.Select(image => image.ImagePath).ToList();
        var createdFiles = new List<string>();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _dbContext.VehicleImages.RemoveRange(imagesToDelete);

            var remainingImages = vehicle.Images
                .Where(image => !deleteIds.Contains(image.VehicleImageId))
                .OrderBy(image => image.SortOrder)
                .ToList();

            var newImages = await SaveNewImagesAsync(
                vehicle.VehicleId,
                model.NewImages,
                remainingImages.Count,
                makeFirstPrimary: remainingImages.Count == 0,
                createdFiles,
                cancellationToken);
            _dbContext.VehicleImages.AddRange(newImages);

            var selectedPrimary = remainingImages.FirstOrDefault(image =>
                model.PrimaryImageId.HasValue
                && image.VehicleImageId == model.PrimaryImageId.Value);
            if (selectedPrimary is null)
            {
                selectedPrimary = remainingImages.FirstOrDefault(image => image.IsPrimary)
                    ?? remainingImages.FirstOrDefault();
            }

            foreach (var image in remainingImages)
            {
                image.IsPrimary = selectedPrimary is not null
                    && image.VehicleImageId == selectedPrimary.VehicleImageId;
            }

            if (selectedPrimary is null && newImages.Count > 0)
            {
                newImages[0].IsPrimary = true;
            }

            var sortOrder = 0;
            foreach (var image in remainingImages)
            {
                image.SortOrder = sortOrder++;
            }
            foreach (var image in newImages)
            {
                image.SortOrder = sortOrder++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            foreach (var oldPath in oldFilePaths)
            {
                DeleteUploadedVehicleFile(oldPath);
            }

            TempData["AdminSuccess"] = $"Đã cập nhật xe {vehicle.VehicleName} · {vehicle.LicensePlate}.";
            return RedirectToAction("Vehicles", "Dashboard");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DeleteFiles(createdFiles);
            ModelState.AddModelError(string.Empty, "Không thể cập nhật xe. Vui lòng thử lại.");
            model.ExistingImages = MapImages(vehicle.Images);
            await PopulateFormDataAsync(model, cancellationToken, includeImages: false);
            return View(model);
        }
    }

    [HttpPost("{id:int}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        int id,
        CancellationToken cancellationToken)
    {
        var vehicle = await _dbContext.Vehicles
            .Include(item => item.Images)
            .Include(item => item.Bookings)
            .Include(item => item.MaintenanceRecords)
            .FirstOrDefaultAsync(item => item.VehicleId == id, cancellationToken);

        if (vehicle is null)
        {
            return NotFound();
        }

        if (vehicle.Bookings.Count > 0 || vehicle.MaintenanceRecords.Count > 0)
        {
            TempData["AdminError"] =
                "Không thể xóa vĩnh viễn xe đã có lịch sử thuê hoặc bảo trì. Hãy chuyển xe sang trạng thái Ngừng hoạt động để giữ dữ liệu đối soát.";
            return RedirectToAction("Vehicles", "Dashboard");
        }

        var imagePaths = vehicle.Images.Select(image => image.ImagePath).ToList();
        _dbContext.Vehicles.Remove(vehicle);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var imagePath in imagePaths)
        {
            DeleteUploadedVehicleFile(imagePath);
        }
        DeleteVehicleUploadFolder(vehicle.VehicleId);

        TempData["AdminSuccess"] = $"Đã xóa xe {vehicle.VehicleName} · {vehicle.LicensePlate}.";
        return RedirectToAction("Vehicles", "Dashboard");
    }

    private async Task PopulateFormDataAsync(
        AdminVehicleFormViewModel model,
        CancellationToken cancellationToken,
        bool includeImages = true)
    {
        model.BrandOptions = await _dbContext.Brands
            .AsNoTracking()
            .Where(brand => brand.IsActive)
            .OrderBy(brand => brand.BrandName)
            .Select(brand => new AdminBrandOptionViewModel
            {
                BrandId = brand.BrandId,
                BrandName = brand.BrandName
            })
            .ToListAsync(cancellationToken);

        if (includeImages && model.VehicleId > 0)
        {
            var images = await _dbContext.VehicleImages
                .AsNoTracking()
                .Where(image => image.VehicleId == model.VehicleId)
                .OrderByDescending(image => image.IsPrimary)
                .ThenBy(image => image.SortOrder)
                .ToListAsync(cancellationToken);
            model.ExistingImages = MapImages(images);
        }
    }

    private async Task<Brand?> ResolveBrandAsync(
        AdminVehicleFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(model.NewBrandName))
        {
            var newBrandName = model.NewBrandName.Trim();
            var normalized = newBrandName.ToLower();
            var existingBrand = await _dbContext.Brands
                .FirstOrDefaultAsync(
                    brand => brand.BrandName.ToLower() == normalized,
                    cancellationToken);

            if (existingBrand is not null)
            {
                existingBrand.IsActive = true;
                return existingBrand;
            }

            var brand = new Brand
            {
                BrandName = newBrandName,
                IsActive = true
            };
            _dbContext.Brands.Add(brand);
            return brand;
        }

        if (!model.BrandId.HasValue)
        {
            ModelState.AddModelError(nameof(model.BrandId), "Vui lòng chọn hãng xe hoặc nhập hãng xe mới.");
            return null;
        }

        var selectedBrand = await _dbContext.Brands
            .FirstOrDefaultAsync(
                brand => brand.BrandId == model.BrandId.Value && brand.IsActive,
                cancellationToken);
        if (selectedBrand is null)
        {
            ModelState.AddModelError(nameof(model.BrandId), "Hãng xe đã chọn không tồn tại hoặc đã ngừng sử dụng.");
        }

        return selectedBrand;
    }

    private static void NormalizeModel(AdminVehicleFormViewModel model)
    {
        model.VehicleName = model.VehicleName?.Trim() ?? string.Empty;
        model.Model = string.IsNullOrWhiteSpace(model.Model) ? null : model.Model.Trim();
        model.LicensePlate = (model.LicensePlate ?? string.Empty).Trim().ToUpperInvariant();
        model.Transmission = model.Transmission?.Trim() ?? string.Empty;
        model.FuelType = model.FuelType?.Trim() ?? string.Empty;
        model.Color = string.IsNullOrWhiteSpace(model.Color) ? null : model.Color.Trim();
        model.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        model.NewBrandName = string.IsNullOrWhiteSpace(model.NewBrandName) ? null : model.NewBrandName.Trim();
        model.NewImages ??= new List<IFormFile>();
        model.DeleteImageIds ??= new List<int>();
    }

    private void ValidateVehicleModel(
        AdminVehicleFormViewModel model,
        bool isCreate,
        int existingImageCount = 0)
    {
        if (model.ManufactureYear > DateTime.Today.Year + 1)
        {
            ModelState.AddModelError(nameof(model.ManufactureYear), "Năm sản xuất không được lớn hơn năm kế tiếp.");
        }

        if (isCreate && model.NewImages.Count == 0)
        {
            ModelState.AddModelError(nameof(model.NewImages), "Vui lòng tải lên ít nhất một ảnh xe.");
        }

        if (existingImageCount + model.NewImages.Count > MaximumImageCount)
        {
            ModelState.AddModelError(nameof(model.NewImages), $"Mỗi xe chỉ được lưu tối đa {MaximumImageCount} ảnh.");
        }

        foreach (var image in model.NewImages)
        {
            if (image.Length <= 0)
            {
                ModelState.AddModelError(nameof(model.NewImages), "Có tệp ảnh rỗng hoặc không hợp lệ.");
                continue;
            }

            if (image.Length > MaximumImageSize)
            {
                ModelState.AddModelError(nameof(model.NewImages), $"Ảnh {image.FileName} vượt quá dung lượng 5 MB.");
            }

            var extension = Path.GetExtension(image.FileName);
            if (!AllowedImageExtensions.Contains(extension))
            {
                ModelState.AddModelError(nameof(model.NewImages), $"Ảnh {image.FileName} không đúng định dạng JPG, JPEG, PNG hoặc WEBP.");
            }

            if (!string.IsNullOrWhiteSpace(image.ContentType)
                && !image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(model.NewImages), $"Tệp {image.FileName} không phải là ảnh hợp lệ.");
            }
        }
    }

    private async Task<List<VehicleImage>> SaveNewImagesAsync(
        int vehicleId,
        IReadOnlyList<IFormFile> files,
        int startSortOrder,
        bool makeFirstPrimary,
        ICollection<string> createdFiles,
        CancellationToken cancellationToken)
    {
        var result = new List<VehicleImage>();
        if (files.Count == 0)
        {
            return result;
        }

        var webRootPath = _environment.WebRootPath
            ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var relativeFolder = Path.Combine("uploads", "vehicles", vehicleId.ToString());
        var absoluteFolder = Path.Combine(webRootPath, relativeFolder);
        Directory.CreateDirectory(absoluteFolder);

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var fileName = $"vehicle-{Guid.NewGuid():N}{extension}";
            var absolutePath = Path.Combine(absoluteFolder, fileName);

            await using (var stream = System.IO.File.Create(absolutePath))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }
            createdFiles.Add(absolutePath);

            var imagePath = "/" + Path.Combine(relativeFolder, fileName).Replace('\\', '/');
            result.Add(new VehicleImage
            {
                VehicleId = vehicleId,
                ImagePath = imagePath,
                IsPrimary = makeFirstPrimary && index == 0,
                SortOrder = startSortOrder + index
            });
        }

        return result;
    }

    private static IReadOnlyList<AdminVehicleImageViewModel> MapImages(
        IEnumerable<VehicleImage> images) => images
        .OrderByDescending(image => image.IsPrimary)
        .ThenBy(image => image.SortOrder)
        .Select(image => new AdminVehicleImageViewModel
        {
            VehicleImageId = image.VehicleImageId,
            ImagePath = NormalizeImagePath(image.ImagePath),
            IsPrimary = image.IsPrimary,
            SortOrder = image.SortOrder
        })
        .ToList();

    private static string NormalizeImagePath(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)
            || imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith('/'))
        {
            return imagePath;
        }

        return "/" + imagePath.TrimStart('~', '/');
    }

    private void DeleteUploadedVehicleFile(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)
            || !imagePath.StartsWith("/uploads/vehicles/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var webRootPath = _environment.WebRootPath
            ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var relativePath = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(webRootPath, relativePath));
        var allowedRoot = Path.GetFullPath(Path.Combine(webRootPath, "uploads", "vehicles"));

        if (!absolutePath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (System.IO.File.Exists(absolutePath))
            {
                System.IO.File.Delete(absolutePath);
            }
        }
        catch
        {
            // Không làm hỏng nghiệp vụ DB chỉ vì file vật lý không xóa được.
        }
    }

    private void DeleteVehicleUploadFolder(int vehicleId)
    {
        var webRootPath = _environment.WebRootPath
            ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var folder = Path.Combine(webRootPath, "uploads", "vehicles", vehicleId.ToString());
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch
        {
            // Thư mục còn sót không được phép làm thất bại thao tác xóa dữ liệu.
        }
    }

    private static void DeleteFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (System.IO.File.Exists(file))
                {
                    System.IO.File.Delete(file);
                }
            }
            catch
            {
                // Dọn file lỗi ở mức best-effort.
            }
        }
    }
}
