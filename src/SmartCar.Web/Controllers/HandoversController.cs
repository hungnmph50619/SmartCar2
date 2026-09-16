using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class HandoversController : Controller
{
    private const int MaximumAdditionalImages = 18;
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private readonly IHandoverService _handoverService;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;
    private readonly ApplicationDbContext _dbContext;

    public HandoversController(
        IHandoverService handoverService,
        IBookingService bookingService,
        IAuditService auditService,
        IWebHostEnvironment environment,
        ApplicationDbContext dbContext)
    {
        _handoverService = handoverService;
        _bookingService = bookingService;
        _auditService = auditService;
        _environment = environment;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Create(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
            return NotFound();

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] = "Chỉ đơn sẵn sàng giao xe mới được lập biên bản giao.";
            return RedirectToBookingDetails(bookingId);
        }

        if (booking.HasHandover)
        {
            TempData["ErrorMessage"] = "Biên bản điện tử đã được lập. Hãy in, ký và tải bản ký.";
            return RedirectToBookingDetails(bookingId);
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] = "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao.";
            return RedirectToBookingDetails(bookingId);
        }

        ViewData["ReceiverDrivingLicenseNumber"] = string.Empty;
        return View(new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = DateTime.Now,
            IncludedKilometers = booking.NumberOfDays * RentalPolicy.IncludedKilometersPerDay,
            ExcessKmFeePerKm = RentalPolicy.ExcessKilometerFee,
            LateReturnFeeMultiplier = RentalPolicy.LateReturnFeeMultiplier,
            TrafficFineTerms = RentalPolicy.TrafficFineTerms,
            DamageCompensationTerms = RentalPolicy.DamageCompensationTerms,
            PenaltyPolicyAccepted = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HandoverViewModel model,
        string? receiverDrivingLicenseNumber,
        bool originalCitizenIdChecked,
        bool originalDrivingLicenseChecked,
        CancellationToken cancellationToken)
    {
        ViewData["ReceiverDrivingLicenseNumber"] = receiverDrivingLicenseNumber?.Trim() ?? string.Empty;

        var booking = await _bookingService.GetAdminBookingAsync(model.BookingId, cancellationToken);
        if (booking is null)
            return NotFound();

        if (booking.Status != BookingStatus.ReadyForPickup || booking.HasHandover)
        {
            TempData["ErrorMessage"] = booking.HasHandover
                ? "Biên bản điện tử đã tồn tại. Hãy tiếp tục bước ký."
                : "Đơn không còn ở trạng thái sẵn sàng giao xe.";
            return RedirectToBookingDetails(model.BookingId);
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] = "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao.";
            return RedirectToBookingDetails(model.BookingId);
        }

        ModelState.Remove(nameof(HandoverViewModel.IncludedKilometers));
        ModelState.Remove(nameof(HandoverViewModel.ExcessKmFeePerKm));
        ModelState.Remove(nameof(HandoverViewModel.LateReturnFeeMultiplier));
        ModelState.Remove(nameof(HandoverViewModel.TrafficFineTerms));
        ModelState.Remove(nameof(HandoverViewModel.DamageCompensationTerms));
        ModelState.Remove(nameof(HandoverViewModel.PenaltyPolicyAccepted));
        ModelState.Remove(nameof(HandoverViewModel.Images));

        model.IncludedKilometers = booking.NumberOfDays * RentalPolicy.IncludedKilometersPerDay;
        model.ExcessKmFeePerKm = RentalPolicy.ExcessKilometerFee;
        model.LateReturnFeeMultiplier = RentalPolicy.LateReturnFeeMultiplier;
        model.TrafficFineTerms = RentalPolicy.TrafficFineTerms;
        model.DamageCompensationTerms = RentalPolicy.DamageCompensationTerms;
        model.PenaltyPolicyAccepted = true;

        var identityFailures = new List<string>();
        var citizenMatches = await CustomerCitizenIdMatchesAsync(
            booking.CustomerId,
            model.ReceiverCitizenId,
            cancellationToken);
        if (!citizenMatches)
        {
            const string message = "CCCD người nhận không trùng với CCCD đã được quản trị viên xác minh của khách đứng tên đơn.";
            ModelState.AddModelError(nameof(HandoverViewModel.ReceiverCitizenId), message);
            identityFailures.Add("CCCD không khớp hồ sơ đã xác minh");
        }

        var licenseMatches = await CustomerDrivingLicenseMatchesAsync(
            booking.CustomerId,
            receiverDrivingLicenseNumber,
            cancellationToken);
        if (!licenseMatches)
        {
            const string message = "GPLX người nhận không trùng với GPLX đã được quản trị viên xác minh của khách đứng tên đơn.";
            ModelState.AddModelError("ReceiverDrivingLicenseNumber", message);
            identityFailures.Add("GPLX không khớp hồ sơ đã xác minh");
        }

        if (!originalCitizenIdChecked)
        {
            ModelState.AddModelError("OriginalCitizenIdChecked", "Nhân viên phải kiểm tra CCCD bản gốc trước khi giao xe.");
            identityFailures.Add("chưa xác nhận CCCD bản gốc");
        }

        if (!originalDrivingLicenseChecked)
        {
            ModelState.AddModelError("OriginalDrivingLicenseChecked", "Nhân viên phải kiểm tra GPLX bản gốc trước khi giao xe.");
            identityFailures.Add("chưa xác nhận GPLX bản gốc");
        }

        if (!model.ReceiverIdentityCheckedInPerson)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.ReceiverIdentityCheckedInPerson),
                "Nhân viên phải xác nhận đúng người có mặt trực tiếp nhận xe.");
            identityFailures.Add("chưa xác nhận đúng người đang trực tiếp nhận xe");
        }

        if (identityFailures.Count > 0)
        {
            await WriteFailedHandoverAttemptAsync(model.BookingId, identityFailures, cancellationToken);
        }

        await ValidateEvidenceImagesAsync(model, cancellationToken);
        if (!ModelState.IsValid)
            return View(model);

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
            return Challenge();

        IReadOnlyList<string> imagePaths;
        try
        {
            imagePaths = await SaveEvidenceImagesAsync(model.BookingId, model, cancellationToken);
        }
        catch
        {
            ModelState.AddModelError(string.Empty, "Không thể lưu đầy đủ ảnh bàn giao. Vui lòng thử lại.");
            return View(model);
        }

        var mileage = model.Mileage!.Value;
        var result = await _handoverService.CreateAsync(
            new CreateHandoverRequest(
                model.BookingId,
                model.HandoverAt,
                mileage,
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
                model.Notes,
                staffId),
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

        await _auditService.WriteAsync(
            staffId,
            "CreateHandover",
            nameof(VehicleHandover),
            model.BookingId.ToString(),
            $"Lập biên bản giao điện tử đơn #{model.BookingId}; đối chiếu CCCD + GPLX bản gốc đúng khách và lưu xác minh trong cùng transaction; " +
            $"{mileage:N0} km; 7 nhóm ảnh bắt buộc và {Math.Max(0, imagePaths.Count - 7)} ảnh bổ sung.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã lưu biên bản và kết quả xác minh người nhận trong cùng một giao dịch. Hãy in, ký và tải bản ký để Staff đối chiếu trước khi bắt đầu chuyến.";
        return RedirectToBookingDetails(model.BookingId);
    }

    [HttpGet]
    public IActionResult Print(int bookingId) =>
        RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId });

    private async Task<bool> CustomerCitizenIdMatchesAsync(
        string customerId,
        string? enteredCitizenId,
        CancellationToken cancellationToken)
    {
        var normalized = new string((enteredCitizenId ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalized.Length != 12)
            return false;

        return await _dbContext.CustomerDocuments.AsNoTracking().AnyAsync(document =>
            document.CustomerId == customerId &&
            document.DocumentType == DocumentTypes.CitizenId &&
            document.Status == DocumentStatus.Verified &&
            document.DocumentNumber == normalized,
            cancellationToken);
    }

    private async Task<bool> CustomerDrivingLicenseMatchesAsync(
        string customerId,
        string? enteredLicenseNumber,
        CancellationToken cancellationToken)
    {
        var normalized = (enteredLicenseNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is < 8 or > 12 || !normalized.All(char.IsLetterOrDigit))
            return false;

        return await _dbContext.CustomerDocuments.AsNoTracking().AnyAsync(document =>
            document.CustomerId == customerId &&
            document.DocumentType == DocumentTypes.DrivingLicense &&
            document.Status == DocumentStatus.Verified &&
            document.DocumentNumber == normalized,
            cancellationToken);
    }

    private async Task WriteFailedHandoverAttemptAsync(
        int bookingId,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            staffId,
            "FailedHandoverIdentityCheck",
            nameof(Booking),
            bookingId.ToString(),
            $"Bàn giao chưa đạt điều kiện danh tính: {string.Join("; ", reasons.Distinct())}. Đơn chưa bị hủy và có thể kiểm tra lại.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }

    private IActionResult RedirectToBookingDetails(int bookingId) =>
        RedirectToAction("Details", "Staff", new { id = bookingId });

    private async Task ValidateEvidenceImagesAsync(
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        await ValidateRequiredImageAsync(model.FrontImage, nameof(HandoverViewModel.FrontImage), "ảnh mặt trước xe", cancellationToken);
        await ValidateRequiredImageAsync(model.RearImage, nameof(HandoverViewModel.RearImage), "ảnh mặt sau xe", cancellationToken);
        await ValidateRequiredImageAsync(model.LeftImage, nameof(HandoverViewModel.LeftImage), "ảnh bên trái xe", cancellationToken);
        await ValidateRequiredImageAsync(model.RightImage, nameof(HandoverViewModel.RightImage), "ảnh bên phải xe", cancellationToken);
        await ValidateRequiredImageAsync(model.InteriorImage, nameof(HandoverViewModel.InteriorImage), "ảnh nội thất", cancellationToken);
        await ValidateRequiredImageAsync(model.OdometerImage, nameof(HandoverViewModel.OdometerImage), "ảnh đồng hồ số km", cancellationToken);
        await ValidateRequiredImageAsync(model.FuelImage, nameof(HandoverViewModel.FuelImage), "ảnh mức nhiên liệu", cancellationToken);

        var additional = model.Images.Where(file => file.Length > 0).ToList();
        if (additional.Count > MaximumAdditionalImages)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                $"Chỉ được tải tối đa {MaximumAdditionalImages} ảnh bổ sung ngoài 7 ảnh bắt buộc.");
        }

        foreach (var image in additional)
        {
            await ValidateImageAsync(image, nameof(HandoverViewModel.Images), cancellationToken);
        }
    }

    private async Task ValidateRequiredImageAsync(
        IFormFile? image,
        string fieldName,
        string vietnameseName,
        CancellationToken cancellationToken)
    {
        if (image is null || image.Length <= 0)
        {
            ModelState.AddModelError(fieldName, $"Vui lòng chọn {vietnameseName}.");
            return;
        }
        await ValidateImageAsync(image, fieldName, cancellationToken);
    }

    private async Task ValidateImageAsync(
        IFormFile image,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(image, MaximumImageBytes, cancellationToken);
        if (error is not null)
        {
            ModelState.AddModelError(fieldName, $"{image.FileName}: {error}");
        }
    }

    private async Task<IReadOnlyList<string>> SaveEvidenceImagesAsync(
        int bookingId,
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/handovers/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var required = new (string Label, IFormFile File)[]
        {
            ("front", model.FrontImage!),
            ("rear", model.RearImage!),
            ("left", model.LeftImage!),
            ("right", model.RightImage!),
            ("interior", model.InteriorImage!),
            ("odometer", model.OdometerImage!),
            ("fuel", model.FuelImage!)
        };

        var paths = new List<string>();
        try
        {
            foreach (var (label, file) in required)
            {
                paths.Add(await SaveOneImageAsync(folder, relativeFolder, label, file, cancellationToken));
            }

            var extras = model.Images.Where(file => file.Length > 0).ToList();
            for (var index = 0; index < extras.Count; index++)
            {
                paths.Add(await SaveOneImageAsync(
                    folder,
                    relativeFolder,
                    $"extra-{index + 1:00}",
                    extras[index],
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

