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

[Authorize(Roles = RoleNames.Staff)]
public sealed class ReturnEditsController : Controller
{
    private const string SignedMarker = "signed-return-";
    private const int MinimumImages = 7;
    private const int MaximumImages = 25;
    private const long MaximumImageBytes = 5 * 1024 * 1024;
    private const int MaximumReasonableKilometersPerDay = 2500;
    private const string AccessoriesComplete = "Đủ";
    private const string AccessoriesMissingValue = "Thiếu";
    private const string AccessoriesMissingPrefix = "Thiếu/mất:";
    private const string ReturnAccessoriesLabel = "Phụ kiện khi trả:";
    private const string ReturnNoteSeparator = " | Ghi chú: ";

    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;

    public ReturnEditsController(
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
            .Include(item => item.VehicleReturn)
            .Include(item => item.Handover)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.VehicleReturn is null || booking.Handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản trả xe để chỉnh sửa.";
            return RedirectToAction("Details", "Staff", new { id = bookingId });
        }

        if (!CanEdit(booking))
        {
            TempData["ErrorMessage"] = "Biên bản trả đã có bản ký, khoản phụ phí đang/đã thanh toán hoặc đơn đã quyết toán nên không thể chỉnh sửa.";
            return RedirectToAction("Inspect", "Returns", new { bookingId });
        }

        var parsedNotes = ParseReturnNotes(booking.VehicleReturn.Notes);
        var parsedAccessory = ParseAccessoryValue(booking.VehicleReturn.AccessoryStatus);
        return View(new ReturnEditViewModel
        {
            BookingId = bookingId,
            ReturnedAt = booking.VehicleReturn.ReturnedAt,
            Mileage = booking.VehicleReturn.Mileage,
            FuelLevel = booking.VehicleReturn.FuelLevel.TrimEnd('%').Trim(),
            AccessoryStatus = parsedAccessory.AccessoryStatus,
            MissingAccessories = parsedAccessory.MissingAccessories,
            HasDamage = booking.VehicleReturn.HasDamage,
            Notes = parsedNotes.Note,
            ExistingImagePaths = ReturnPhotos(booking.VehicleReturn.ImagePaths).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ReturnEditViewModel model, CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(ReturnEditViewModel.ExistingImagePaths));
        ModelState.Remove(nameof(ReturnEditViewModel.ImagesToDelete));
        ModelState.Remove(nameof(ReturnEditViewModel.NewImages));

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
                .ThenInclude(item => item!.AdditionalCharges)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == model.BookingId, cancellationToken);

        if (booking?.VehicleReturn is null || booking.Handover is null)
        {
            return NotFound();
        }

        if (!CanEdit(booking))
        {
            TempData["ErrorMessage"] = "Biên bản trả đã có bản ký, khoản phụ phí đang/đã thanh toán hoặc đơn đã quyết toán nên không thể chỉnh sửa.";
            return RedirectToAction("Inspect", "Returns", new { bookingId = model.BookingId });
        }

        var currentPaths = SplitPaths(booking.VehicleReturn.ImagePaths).ToList();
        var returnPhotos = ReturnPhotos(booking.VehicleReturn.ImagePaths).ToList();
        var deleteSet = (model.ImagesToDelete ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (deleteSet.Any(path => !returnPhotos.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(model.ImagesToDelete), "Danh sách ảnh cần xóa không hợp lệ.");
        }

        var newImages = (model.NewImages ?? new List<IFormFile>())
            .Where(file => file.Length > 0)
            .ToList();

        var finalImageCount = returnPhotos.Count - deleteSet.Count + newImages.Count;
        if (finalImageCount < MinimumImages)
        {
            ModelState.AddModelError(
                nameof(model.NewImages),
                $"Biên bản trả xe phải giữ tối thiểu {MinimumImages} ảnh đối chiếu.");
        }

        if (finalImageCount > MaximumImages)
        {
            ModelState.AddModelError(
                nameof(model.NewImages),
                $"Biên bản chỉ được lưu tối đa {MaximumImages} ảnh đối chiếu.");
        }

        foreach (var image in newImages)
        {
            var error = await ImageFileValidator.ValidateAsync(image, MaximumImageBytes, cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(model.NewImages), $"{image.FileName}: {error}");
            }
        }

        if (!model.Mileage.HasValue || model.Mileage.Value < booking.Handover.Mileage)
        {
            ModelState.AddModelError(nameof(model.Mileage), $"Số km trả không được nhỏ hơn số km lúc giao ({booking.Handover.Mileage:N0} km).");
        }

        if (model.ReturnedAt < booking.Handover.HandoverAt)
        {
            ModelState.AddModelError(nameof(model.ReturnedAt), "Thời gian trả không được trước thời gian giao xe.");
        }

        if (model.ReturnedAt > DateTime.Now.AddMinutes(5))
        {
            ModelState.AddModelError(nameof(model.ReturnedAt), "Thời gian trả xe không được ở tương lai.");
        }

        if (model.Mileage.HasValue && model.ReturnedAt >= booking.Handover.HandoverAt)
        {
            var elapsedDays = Math.Max(
                1,
                (int)Math.Ceiling((model.ReturnedAt - booking.Handover.HandoverAt).TotalHours / 24d));
            var drivenKilometers = model.Mileage.Value - booking.Handover.Mileage;
            var maximumReasonableKilometers = elapsedDays * MaximumReasonableKilometersPerDay;
            if (drivenKilometers > maximumReasonableKilometers)
            {
                ModelState.AddModelError(
                    nameof(model.Mileage),
                    $"Số km tăng {drivenKilometers:N0} km trong {elapsedDays} ngày là bất thường. Vui lòng kiểm tra lại.");
            }
        }

        if (!TryParseFuel(model.FuelLevel, out var fuelPercent))
        {
            ModelState.AddModelError(nameof(model.FuelLevel), "Mức nhiên liệu phải từ 0 đến 100%." );
        }

        if (model.AccessoryStatus == AccessoriesMissingValue && string.IsNullOrWhiteSpace(model.MissingAccessories))
        {
            ModelState.AddModelError(nameof(model.MissingAccessories), "Vui lòng ghi rõ phụ kiện bị thiếu hoặc mất.");
        }

        if (!ModelState.IsValid)
        {
            PopulateExistingImages(model, booking.VehicleReturn);
            return View(model);
        }

        var addedPaths = new List<string>();
        try
        {
            addedPaths = await SaveImagesAsync(model.BookingId, newImages, cancellationToken);

            var updatedPaths = currentPaths
                .Where(path => !deleteSet.Contains(path))
                .Concat(addedPaths)
                .ToList();

            var normalizedAccessory = NormalizeAccessoryValue(model.AccessoryStatus, model.MissingAccessories);

            booking.VehicleReturn.ReturnedAt = model.ReturnedAt;
            booking.VehicleReturn.Mileage = model.Mileage!.Value;
            booking.VehicleReturn.FuelLevel = $"{fuelPercent}%";
            booking.VehicleReturn.HasDamage = model.HasDamage;
            booking.VehicleReturn.AccessoryStatus = normalizedAccessory;
            booking.VehicleReturn.Notes = BuildReturnNotes(model.AccessoryStatus, model.MissingAccessories, model.Notes);
            booking.VehicleReturn.ImagePaths = string.Join(';', updatedPaths);
            booking.Vehicle.CurrentMileage = model.Mileage.Value;

            RecalculateAutomaticCharges(booking);
            SynchronizePendingAdditionalChargePayment(booking);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DeletePhysicalFiles(addedPaths);
            throw;
        }

        DeletePhysicalFiles(deleteSet);

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            staffId,
            "StaffEditReturn",
            nameof(VehicleReturn),
            model.BookingId.ToString(),
            $"Nhân viên chỉnh sửa biên bản trả xe chưa ký của đơn #{model.BookingId}. Xóa {deleteSet.Count} ảnh, thêm {addedPaths.Count} ảnh.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật biên bản trả xe và đồng bộ lại phụ phí. Hãy kiểm tra lại trước khi in và ký.";
        return RedirectToAction("Inspect", "Returns", new { bookingId = model.BookingId });
    }

    private static bool CanEdit(Booking booking) =>
        booking.Status == BookingStatus.PendingInspection &&
        booking.VehicleReturn is not null &&
        !SplitPaths(booking.VehicleReturn.ImagePaths).Any(path => path.Contains(SignedMarker, StringComparison.OrdinalIgnoreCase)) &&
        !booking.Payments.Any(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Method != PaymentMethods.DepositDeduction &&
            payment.Status is PaymentStatus.AwaitingConfirmation or PaymentStatus.Paid);

    private void SynchronizePendingAdditionalChargePayment(Booking booking)
    {
        var pendingPayment = booking.Payments.FirstOrDefault(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Method != PaymentMethods.DepositDeduction &&
            payment.Status == PaymentStatus.Pending);

        if (booking.AdditionalAmount > 0)
        {
            if (pendingPayment is null)
            {
                booking.Payments.Add(new Payment
                {
                    BookingId = booking.BookingId,
                    Type = PaymentType.AdditionalCharge,
                    Amount = booking.AdditionalAmount,
                    Method = PaymentMethods.NotSelected,
                    Status = PaymentStatus.Pending
                });
            }
            else
            {
                pendingPayment.Amount = booking.AdditionalAmount;
            }
        }
        else if (pendingPayment is not null)
        {
            _dbContext.Payments.Remove(pendingPayment);
        }
    }

    private static IReadOnlyList<string> SplitPaths(string? imagePaths) =>
        string.IsNullOrWhiteSpace(imagePaths)
            ? Array.Empty<string>()
            : imagePaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> ReturnPhotos(string? imagePaths) =>
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

    private static (string AccessoryStatus, string? MissingAccessories) ParseAccessoryValue(string? value)
    {
        var raw = value?.Trim();
        if (!string.IsNullOrWhiteSpace(raw) &&
            raw.StartsWith(AccessoriesMissingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (
                AccessoriesMissingValue,
                raw[AccessoriesMissingPrefix.Length..].Trim());
        }

        return (AccessoriesComplete, null);
    }

    private static string NormalizeAccessoryValue(string accessoryStatus, string? missingAccessories) =>
        accessoryStatus == AccessoriesMissingValue
            ? $"{AccessoriesMissingPrefix} {missingAccessories?.Trim()}".Trim()
            : AccessoriesComplete;

    private static (string AccessoryStatus, string? MissingAccessories, string? Note) ParseReturnNotes(string? notes)
    {
        var raw = notes?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith(ReturnAccessoriesLabel, StringComparison.OrdinalIgnoreCase))
        {
            return (AccessoriesComplete, null, raw);
        }

        var body = raw[ReturnAccessoriesLabel.Length..].Trim();
        var separatorIndex = body.IndexOf(ReturnNoteSeparator, StringComparison.Ordinal);
        var accessoryStatus = separatorIndex >= 0 ? body[..separatorIndex].Trim() : body;
        var note = separatorIndex >= 0 ? body[(separatorIndex + ReturnNoteSeparator.Length)..].Trim() : null;

        if (accessoryStatus.StartsWith(AccessoriesMissingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (
                AccessoriesMissingValue,
                accessoryStatus[AccessoriesMissingPrefix.Length..].Trim(),
                string.IsNullOrWhiteSpace(note) ? null : note);
        }

        return (AccessoriesComplete, null, string.IsNullOrWhiteSpace(note) ? null : note);
    }

    private static string BuildReturnNotes(string accessoryStatus, string? missingAccessories, string? note)
    {
        var normalizedAccessory = NormalizeAccessoryValue(accessoryStatus, missingAccessories);
        var normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return normalizedNote is null
            ? $"{ReturnAccessoriesLabel} {normalizedAccessory}"
            : $"{ReturnAccessoriesLabel} {normalizedAccessory}{ReturnNoteSeparator}{normalizedNote}";
    }

    private static void PopulateExistingImages(ReturnEditViewModel model, VehicleReturn record)
    {
        model.ExistingImagePaths = ReturnPhotos(record.ImagePaths).ToList();
    }

    private async Task<List<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images.Where(file => file.Length > 0).ToList();
        var paths = new List<string>();
        if (selectedImages.Count == 0)
        {
            return paths;
        }

        var relativeFolder = $"uploads/returns/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        try
        {
            foreach (var image in selectedImages)
            {
                var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
                var fileName = $"other-edit-{Guid.NewGuid():N}{extension}";
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

    private static void RecalculateAutomaticCharges(Booking booking)
    {
        if (booking.VehicleReturn is null || booking.Handover is null)
        {
            return;
        }

        var vehicleReturn = booking.VehicleReturn;
        var lateMinutes = vehicleReturn.ReturnedAt > booking.ReturnDate
            ? (int)Math.Ceiling((vehicleReturn.ReturnedAt - booking.ReturnDate).TotalMinutes)
            : 0;
        var lateDays = lateMinutes > 0
            ? Math.Max(1, (int)Math.Ceiling(lateMinutes / 1440d))
            : 0;
        var lateMultiplier = booking.Handover.LateReturnFeeMultiplier >= 1
            ? booking.Handover.LateReturnFeeMultiplier
            : RentalPolicy.LateReturnFeeMultiplier;
        var lateFee = lateDays * booking.DailyPrice * lateMultiplier;

        vehicleReturn.IsLateReturn = lateMinutes > 0;
        vehicleReturn.LateMinutes = lateMinutes;
        vehicleReturn.LateFee = lateFee;

        foreach (var charge in vehicleReturn.AdditionalCharges
                     .Where(charge => charge.ChargeType is AdditionalChargeType.LateReturn or AdditionalChargeType.ExcessMileage)
                     .ToList())
        {
            vehicleReturn.AdditionalCharges.Remove(charge);
        }

        if (lateFee > 0)
        {
            vehicleReturn.AdditionalCharges.Add(new AdditionalCharge
            {
                ChargeType = AdditionalChargeType.LateReturn,
                Description = $"Phí trả xe muộn {lateMinutes} phút ({lateDays} ngày tính phí x {lateMultiplier:0.##}).",
                Amount = lateFee
            });
        }

        var drivenKilometers = Math.Max(0, vehicleReturn.Mileage - booking.Handover.Mileage);
        var paidRentalDays = Math.Max(
            1,
            (int)Math.Ceiling((booking.ReturnDate - booking.PickupDate).TotalHours / 24d));
        var effectiveIncludedKilometers = Math.Max(
            booking.Handover.IncludedKilometers,
            paidRentalDays * RentalPolicy.IncludedKilometersPerDay);
        var excessKilometers = Math.Max(0, drivenKilometers - effectiveIncludedKilometers);
        var excessMileageFee = excessKilometers * booking.Handover.ExcessKmFeePerKm;

        if (excessMileageFee > 0)
        {
            vehicleReturn.AdditionalCharges.Add(new AdditionalCharge
            {
                ChargeType = AdditionalChargeType.ExcessMileage,
                Description = $"Phí vượt {excessKilometers:N0} km so với định mức {effectiveIncludedKilometers:N0} km x {booking.Handover.ExcessKmFeePerKm:N0} đ/km.",
                Amount = excessMileageFee
            });
        }

        var storedDeliveryFee = Math.Max(
            0m,
            booking.TotalAmount - booking.RentalAmount - booking.AdditionalAmount);
        booking.AdditionalAmount = vehicleReturn.AdditionalCharges.Sum(charge => charge.Amount);
        booking.TotalAmount = Math.Max(
            0m,
            booking.RentalAmount + storedDeliveryFee + booking.AdditionalAmount);
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