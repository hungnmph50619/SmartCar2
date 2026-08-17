using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class HandoversController : Controller
{
    private const int MaximumImages = 14;
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private readonly IHandoverService _handoverService;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;

    public HandoversController(
        IHandoverService handoverService,
        IBookingService bookingService,
        IAuditService auditService,
        IWebHostEnvironment environment)
    {
        _handoverService = handoverService;
        _bookingService = bookingService;
        _auditService = auditService;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> Create(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(
            bookingId,
            cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn sẵn sàng giao xe mới được lập biên bản giao.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (booking.HasHandover)
        {
            TempData["ErrorMessage"] =
                "Biên bản điện tử đã được lập. Hãy in, ký và tải bản ký.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View(new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = DateTime.Now > booking.PickupDate
                ? DateTime.Now
                : booking.PickupDate,
            IncludedKilometers = booking.NumberOfDays * RentalPolicy.IncludedKilometersPerDay,
            ExcessKmFeePerKm = RentalPolicy.ExcessKilometerFee,
            LateReturnFeeMultiplier = RentalPolicy.LateReturnFeeMultiplier,
            TrafficFineTerms = RentalPolicy.TrafficFineTerms,
            DamageCompensationTerms = RentalPolicy.DamageCompensationTerms
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(
            model.BookingId,
            cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup || booking.HasHandover)
        {
            TempData["ErrorMessage"] =
                booking.HasHandover
                    ? "Biên bản điện tử đã tồn tại. Hãy tiếp tục bước ký."
                    : "Đơn không còn ở trạng thái sẵn sàng giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao.";
            return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
        }

        // Các mức phí luôn lấy từ server, không nhận giá trị chính sách do browser gửi lên.
        ModelState.Remove(nameof(HandoverViewModel.IncludedKilometers));
        ModelState.Remove(nameof(HandoverViewModel.ExcessKmFeePerKm));
        ModelState.Remove(nameof(HandoverViewModel.LateReturnFeeMultiplier));
        ModelState.Remove(nameof(HandoverViewModel.TrafficFineTerms));
        ModelState.Remove(nameof(HandoverViewModel.DamageCompensationTerms));

        model.IncludedKilometers = booking.NumberOfDays * RentalPolicy.IncludedKilometersPerDay;
        model.ExcessKmFeePerKm = RentalPolicy.ExcessKilometerFee;
        model.LateReturnFeeMultiplier = RentalPolicy.LateReturnFeeMultiplier;
        model.TrafficFineTerms = RentalPolicy.TrafficFineTerms;
        model.DamageCompensationTerms = RentalPolicy.DamageCompensationTerms;

        var evidenceFiles = BuildEvidenceFiles(model);
        await ValidateImagesAsync(evidenceFiles, model.Images, cancellationToken);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        IReadOnlyList<string> imagePaths;
        try
        {
            imagePaths = await SaveImagesAsync(
                model.BookingId,
                evidenceFiles,
                model.Images,
                cancellationToken);
        }
        catch
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                "Không thể lưu ảnh bàn giao. Vui lòng thử lại.");
            return View(model);
        }

        var result = await _handoverService.CreateAsync(
            new CreateHandoverRequest(
                model.BookingId,
                model.HandoverAt,
                model.Mileage,
                model.FuelLevel,
                model.ExteriorCondition,
                model.InteriorCondition,
                model.Accessories,
                string.Join(';', imagePaths),
                model.IncludedKilometers,
                model.ExcessKmFeePerKm,
                model.LateReturnFeeMultiplier,
                model.TrafficFineTerms,
                model.DamageCompensationTerms,
                model.PenaltyPolicyAccepted,
                model.Notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedImages(imagePaths);
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
            return View(model);
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "CreateHandover",
            nameof(VehicleHandover),
            model.BookingId.ToString(),
            $"Lập biên bản giao điện tử đơn #{model.BookingId}, {model.Mileage:N0} km, {imagePaths.Count} ảnh chứng cứ.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã lưu biên bản điện tử. Hãy in, ký và tải bản ký để bắt đầu chuyến thuê.";

        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    // Giữ URL cũ để các liên kết cũ vẫn hoạt động.
    [HttpGet]
    public IActionResult Print(int bookingId) =>
        RedirectToAction(
            "HandoverPrint",
            "AdminRentalDocuments",
            new { bookingId });

    private static IReadOnlyList<(string Label, string FieldName, IFormFile? File)> BuildEvidenceFiles(
        HandoverViewModel model) =>
        new (string, string, IFormFile?)[]
        {
            ("front", nameof(HandoverViewModel.FrontImage), model.FrontImage),
            ("rear", nameof(HandoverViewModel.RearImage), model.RearImage),
            ("left", nameof(HandoverViewModel.LeftImage), model.LeftImage),
            ("right", nameof(HandoverViewModel.RightImage), model.RightImage),
            ("interior", nameof(HandoverViewModel.InteriorImage), model.InteriorImage),
            ("odometer", nameof(HandoverViewModel.OdometerImage), model.OdometerImage),
            ("fuel", nameof(HandoverViewModel.FuelImage), model.FuelImage)
        };

    private async Task ValidateImagesAsync(
        IReadOnlyList<(string Label, string FieldName, IFormFile? File)> evidenceFiles,
        IReadOnlyCollection<IFormFile> otherImages,
        CancellationToken cancellationToken)
    {
        foreach (var evidence in evidenceFiles)
        {
            if (evidence.File is null || evidence.File.Length == 0)
            {
                ModelState.AddModelError(evidence.FieldName, "Cần ảnh này để đối chiếu khi trả xe.");
                continue;
            }

            await ValidateImageAsync(evidence.File, evidence.FieldName, cancellationToken);
        }

        var selectedOtherImages = otherImages.Where(file => file.Length > 0).ToList();
        if (evidenceFiles.Count + selectedOtherImages.Count > MaximumImages)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                $"Tổng số ảnh tối đa là {MaximumImages}.");
        }

        foreach (var image in selectedOtherImages)
        {
            await ValidateImageAsync(image, nameof(HandoverViewModel.Images), cancellationToken);
        }
    }

    private async Task ValidateImageAsync(
        IFormFile image,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(
            image,
            MaximumImageBytes,
            cancellationToken);

        if (error is not null)
        {
            ModelState.AddModelError(fieldName, $"{image.FileName}: {error}");
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IReadOnlyList<(string Label, string FieldName, IFormFile? File)> evidenceFiles,
        IEnumerable<IFormFile> otherImages,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/handovers/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var paths = new List<string>();
        try
        {
            foreach (var evidence in evidenceFiles.Where(item => item.File is { Length: > 0 }))
            {
                paths.Add(await SaveOneImageAsync(
                    folder,
                    relativeFolder,
                    evidence.Label,
                    evidence.File!,
                    cancellationToken));
            }

            foreach (var image in otherImages.Where(file => file.Length > 0))
            {
                paths.Add(await SaveOneImageAsync(
                    folder,
                    relativeFolder,
                    "other",
                    image,
                    cancellationToken));
            }

            return paths;
        }
        catch
        {
            DeleteSavedImages(paths);
            throw;
        }
    }

    private static async Task<string> SaveOneImageAsync(
        string folder,
        string relativeFolder,
        string label,
        IFormFile image,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var fileName = $"{label}-{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(folder, fileName);

        await using var stream = System.IO.File.Create(fullPath);
        await image.CopyToAsync(stream, cancellationToken);
        return $"/{relativeFolder}/{fileName}";
    }

    private void DeleteSavedImages(IEnumerable<string> imagePaths)
    {
        foreach (var imagePath in imagePaths)
        {
            var fullPath = Path.Combine(
                _environment.WebRootPath,
                imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }
    }
}
