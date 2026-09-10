using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class HandoverEditsController : Controller
{
    private const string SignedMarker = "signed-handover-";
    private const int MinimumImages = 7;
    private const int MaximumImages = 25;
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;

    public HandoverEditsController(
        ApplicationDbContext dbContext,
        IAuditService auditService,
        IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int bookingId, CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (!CanEdit(booking.Status, booking.Handover.ImagePaths))
        {
            TempData["ErrorMessage"] = "Biên bản đã được ký hoặc chuyến đã bắt đầu nên không thể chỉnh sửa.";
            return RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId });
        }

        var record = booking.Handover;
        return View(new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = record.HandoverAt,
            Mileage = record.Mileage,
            FuelLevel = record.FuelLevel.TrimEnd('%').Trim(),
            ExteriorCondition = record.ExteriorCondition,
            InteriorCondition = record.InteriorCondition,
            Accessories = record.Accessories,
            IncludedKilometers = record.IncludedKilometers,
            ExcessKmFeePerKm = record.ExcessKmFeePerKm,
            LateReturnFeeMultiplier = record.LateReturnFeeMultiplier,
            TrafficFineTerms = record.TrafficFineTerms,
            DamageCompensationTerms = record.DamageCompensationTerms,
            PenaltyPolicyAccepted = true,
            Notes = record.Notes,
            ExistingImagePaths = VehiclePhotos(record.ImagePaths).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(HandoverViewModel model, CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(HandoverViewModel.Images));
        ModelState.Remove(nameof(HandoverViewModel.NewImages));
        ModelState.Remove(nameof(HandoverViewModel.ExistingImagePaths));
        ModelState.Remove(nameof(HandoverViewModel.ImagesToDelete));
        ModelState.Remove(nameof(HandoverViewModel.FrontImage));
        ModelState.Remove(nameof(HandoverViewModel.RearImage));
        ModelState.Remove(nameof(HandoverViewModel.LeftImage));
        ModelState.Remove(nameof(HandoverViewModel.RightImage));
        ModelState.Remove(nameof(HandoverViewModel.InteriorImage));
        ModelState.Remove(nameof(HandoverViewModel.OdometerImage));
        ModelState.Remove(nameof(HandoverViewModel.FuelImage));
        ModelState.Remove(nameof(HandoverViewModel.IncludedKilometers));
        ModelState.Remove(nameof(HandoverViewModel.ExcessKmFeePerKm));
        ModelState.Remove(nameof(HandoverViewModel.LateReturnFeeMultiplier));
        ModelState.Remove(nameof(HandoverViewModel.TrafficFineTerms));
        ModelState.Remove(nameof(HandoverViewModel.DamageCompensationTerms));
        ModelState.Remove(nameof(HandoverViewModel.PenaltyPolicyAccepted));

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == model.BookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            return NotFound();
        }

        if (!CanEdit(booking.Status, booking.Handover.ImagePaths))
        {
            TempData["ErrorMessage"] = "Biên bản đã được ký hoặc chuyến đã bắt đầu nên không thể chỉnh sửa.";
            return RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId = model.BookingId });
        }

        var currentPaths = SplitPaths(booking.Handover.ImagePaths).ToList();
        var vehiclePhotos = VehiclePhotos(booking.Handover.ImagePaths).ToList();
        var imagesToDelete = model.ImagesToDelete ?? new List<string>();
        var deleteSet = imagesToDelete
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (deleteSet.Any(path => !vehiclePhotos.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(model.ImagesToDelete), "Danh sách ảnh cần xóa không hợp lệ.");
        }

        var newImages = (model.NewImages ?? new List<IFormFile>())
            .Where(file => file.Length > 0)
            .ToList();

        var finalImageCount = vehiclePhotos.Count - deleteSet.Count + newImages.Count;
        if (finalImageCount < MinimumImages)
        {
            ModelState.AddModelError(
                nameof(model.NewImages),
                $"Biên bản giao xe phải giữ tối thiểu {MinimumImages} ảnh đối chiếu.");
        }

        if (finalImageCount > MaximumImages)
        {
            ModelState.AddModelError(
                nameof(model.NewImages),
                $"Biên bản chỉ được lưu tối đa {MaximumImages} ảnh đối chiếu.");
        }

        foreach (var image in newImages)
        {
            var error = await ImageFileValidator.ValidateAsync(
                image,
                MaximumImageBytes,
                cancellationToken);

            if (error is not null)
            {
                ModelState.AddModelError(nameof(model.NewImages), $"{image.FileName}: {error}");
            }
        }

        if (model.HandoverAt > DateTime.Now.AddMinutes(5))
        {
            ModelState.AddModelError(nameof(model.HandoverAt), "Thời gian giao xe không được ở tương lai.");
        }

        if (model.HandoverAt < booking.PickupDate || model.HandoverAt >= booking.ReturnDate)
        {
            ModelState.AddModelError(nameof(model.HandoverAt), "Thời gian giao phải nằm trong khoảng thuê đã đặt.");
        }

        if (!model.Mileage.HasValue || model.Mileage.Value < booking.Vehicle.CurrentMileage)
        {
            ModelState.AddModelError(nameof(model.Mileage), $"Số km không được nhỏ hơn số km hiện tại ({booking.Vehicle.CurrentMileage:N0} km).");
        }

        if (!TryParseFuel(model.FuelLevel, out var fuelPercent))
        {
            ModelState.AddModelError(nameof(model.FuelLevel), "Mức nhiên liệu phải từ 0 đến 100%.");
        }

        if (!ModelState.IsValid)
        {
            PopulatePolicyFields(model, booking.Handover);
            return View(model);
        }

        var addedPaths = new List<string>();
        try
        {
            addedPaths = await SaveImagesAsync(
                model.BookingId,
                newImages,
                cancellationToken);

            var updatedPaths = currentPaths
                .Where(path => !deleteSet.Contains(path))
                .Concat(addedPaths)
                .ToList();

            booking.Handover.HandoverAt = model.HandoverAt;
            booking.Handover.Mileage = model.Mileage!.Value;
            booking.Handover.FuelLevel = $"{fuelPercent}%";
            booking.Handover.Accessories = Normalize(model.Accessories);
            booking.Handover.Notes = Normalize(model.Notes);
            booking.Handover.ImagePaths = string.Join(';', updatedPaths);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DeletePhysicalFiles(addedPaths);
            throw;
        }

        DeletePhysicalFiles(deleteSet);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "EditHandover",
            nameof(VehicleHandover),
            model.BookingId.ToString(),
            $"Chỉnh sửa biên bản giao xe chưa ký của đơn #{model.BookingId}. Xóa {deleteSet.Count} ảnh, thêm {addedPaths.Count} ảnh.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật biên bản giao xe. Hãy kiểm tra lại trước khi in và ký.";
        return RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId = model.BookingId });
    }

    private static bool CanEdit(BookingStatus status, string? imagePaths) =>
        status == BookingStatus.ReadyForPickup &&
        !SplitPaths(imagePaths).Any(path => path.Contains(SignedMarker, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> SplitPaths(string? imagePaths) =>
        string.IsNullOrWhiteSpace(imagePaths)
            ? Array.Empty<string>()
            : imagePaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> VehiclePhotos(string? imagePaths) =>
        SplitPaths(imagePaths)
            .Where(path => !path.Contains(SignedMarker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool TryParseFuel(string? value, out int percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().TrimEnd('%').Trim();
        return int.TryParse(normalized, out percent) && percent is >= 0 and <= 100;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void PopulatePolicyFields(HandoverViewModel model, VehicleHandover record)
    {
        model.IncludedKilometers = record.IncludedKilometers;
        model.ExcessKmFeePerKm = record.ExcessKmFeePerKm;
        model.LateReturnFeeMultiplier = record.LateReturnFeeMultiplier;
        model.TrafficFineTerms = record.TrafficFineTerms;
        model.DamageCompensationTerms = record.DamageCompensationTerms;
        model.PenaltyPolicyAccepted = true;
        model.ExistingImagePaths = VehiclePhotos(record.ImagePaths).ToList();
    }

    private async Task<List<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images
            .Where(file => file.Length > 0)
            .ToList();

        var paths = new List<string>();
        if (selectedImages.Count == 0)
        {
            return paths;
        }

        var relativeFolder = $"uploads/handovers/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        try
        {
            foreach (var image in selectedImages)
            {
                var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
                var fileName = $"evidence-edit-{Guid.NewGuid():N}{extension}";
                var fullPath = Path.Combine(folder, fileName);

                await using var stream = System.IO.File.Create(fullPath);
                await image.CopyToAsync(stream, cancellationToken);
                paths.Add($"/{relativeFolder}/{fileName}");
            }
        }
        catch
        {
            DeletePhysicalFiles(paths);
            throw;
        }

        return paths;
    }

    private void DeletePhysicalFiles(IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            DeletePhysicalFile(relativePath);
        }
    }

    private void DeletePhysicalFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var fullPath = Path.Combine(
            _environment.WebRootPath,
            relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }
    }
}
