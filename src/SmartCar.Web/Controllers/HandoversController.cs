using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Handovers;
using SmartCar.Application.Features.Operations;
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
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] = "Chỉ đơn sẵn sàng giao xe mới được lập biên bản giao.";
            return RedirectToBookingDetails(bookingId);
        }

        if (booking.HasHandover)
        {
            TempData["ErrorMessage"] = "Biên bản điện tử đã được lập. Hãy tiếp tục bước ký và xác minh bản ký.";
            return RedirectToBookingDetails(bookingId);
        }

        if (!BookingWorkflowRules.CanPrepareHandover(DateTime.Now, booking.PickupDate, booking.ReturnDate))
        {
            TempData["ErrorMessage"] = $"Chưa đến giờ nhận xe ({booking.PickupDate:dd/MM/yyyy HH:mm}); chưa thể lập biên bản giao.";
            return RedirectToBookingDetails(bookingId);
        }

        var model = new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = DateTime.Now,
            IncludedKilometersPerDay = booking.Policy.IncludedKilometersPerDay,
            LateReturnGraceMinutes = booking.Policy.LateReturnGraceMinutes,
            IncludedKilometers = booking.NumberOfDays * booking.Policy.IncludedKilometersPerDay,
            ExcessKmFeePerKm = booking.Policy.ExcessKilometerFee,
            LateReturnFeeMultiplier = booking.Policy.LateReturnFeeMultiplier,
            TrafficFineTerms = booking.Policy.TrafficFineTerms,
            DamageCompensationTerms = booking.Policy.DamageCompensationTerms,
            PenaltyPolicyAccepted = true
        };

        if (!await PopulateVerifiedIdentityAsync(model, booking, cancellationToken))
        {
            TempData["ErrorMessage"] =
                "Khách chưa có đủ CCCD và GPLX đã được Admin xác minh, hoặc giấy tờ không còn hiệu lực đến ngày trả xe. Không thể bàn giao.";
            return RedirectToBookingDetails(bookingId);
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(model.BookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup || booking.HasHandover)
        {
            TempData["ErrorMessage"] = booking.HasHandover
                ? "Biên bản điện tử đã tồn tại. Hãy tiếp tục bước ký."
                : "Đơn không còn ở trạng thái sẵn sàng giao xe.";
            return RedirectToBookingDetails(model.BookingId);
        }

        if (!BookingWorkflowRules.CanPrepareHandover(DateTime.Now, booking.PickupDate, booking.ReturnDate))
        {
            TempData["ErrorMessage"] = $"Chưa đến giờ nhận xe ({booking.PickupDate:dd/MM/yyyy HH:mm}); chưa thể lập biên bản giao.";
            return RedirectToBookingDetails(model.BookingId);
        }

        ModelState.Remove(nameof(HandoverViewModel.CustomerId));
        ModelState.Remove(nameof(HandoverViewModel.VerifiedCustomerName));
        ModelState.Remove(nameof(HandoverViewModel.VerifiedCitizenId));
        ModelState.Remove(nameof(HandoverViewModel.VerifiedDrivingLicenseNumber));
        ModelState.Remove(nameof(HandoverViewModel.IncludedKilometers));
        ModelState.Remove(nameof(HandoverViewModel.ExcessKmFeePerKm));
        ModelState.Remove(nameof(HandoverViewModel.LateReturnFeeMultiplier));
        ModelState.Remove(nameof(HandoverViewModel.TrafficFineTerms));
        ModelState.Remove(nameof(HandoverViewModel.DamageCompensationTerms));
        ModelState.Remove(nameof(HandoverViewModel.PenaltyPolicyAccepted));
        ModelState.Remove(nameof(HandoverViewModel.Images));
        model.Images ??= new();

        model.HandoverAt = DateTime.Now;
        model.IncludedKilometersPerDay = booking.Policy.IncludedKilometersPerDay;
        model.LateReturnGraceMinutes = booking.Policy.LateReturnGraceMinutes;
        model.IncludedKilometers = booking.NumberOfDays * booking.Policy.IncludedKilometersPerDay;
        model.ExcessKmFeePerKm = booking.Policy.ExcessKilometerFee;
        model.LateReturnFeeMultiplier = booking.Policy.LateReturnFeeMultiplier;
        model.TrafficFineTerms = booking.Policy.TrafficFineTerms;
        model.DamageCompensationTerms = booking.Policy.DamageCompensationTerms;
        model.PenaltyPolicyAccepted = true;

        var identityFailures = new List<string>();
        if (!await PopulateVerifiedIdentityAsync(model, booking, cancellationToken))
        {
            ModelState.AddModelError(
                string.Empty,
                "Hồ sơ KYC của khách không còn đủ điều kiện bàn giao. Vui lòng dừng giao xe và kiểm tra lại hồ sơ.");
            identityFailures.Add("hồ sơ KYC không còn hợp lệ");
        }

        if (!model.OriginalCitizenIdChecked)
        {
            identityFailures.Add("chưa xác nhận CCCD bản gốc");
        }

        if (!model.OriginalDrivingLicenseChecked)
        {
            identityFailures.Add("chưa xác nhận GPLX bản gốc");
        }

        if (!model.ReceiverIdentityCheckedInPerson)
        {
            identityFailures.Add("chưa xác nhận đúng người đang trực tiếp nhận xe");
        }

        await ValidateIdentityFaceSessionAsync(model, booking, identityFailures, cancellationToken);
        await ValidateEvidenceImagesAsync(model, cancellationToken);

        if (identityFailures.Count > 0)
        {
            await WriteFailedHandoverAttemptAsync(model.BookingId, identityFailures, cancellationToken);
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

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
                staffId,
                model.IdentityFaceSessionId!.Value),
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
            $"Lập biên bản giao điện tử đơn #{model.BookingId}; Staff đối chiếu CCCD + GPLX bản gốc với hồ sơ KYC đã xác minh, " +
            $"chụp ảnh mặt người nhận trực tiếp và consume phiên ảnh một lần trong cùng transaction; {mileage:N0} km; " +
            $"7 nhóm ảnh bắt buộc và {Math.Max(0, imagePaths.Count - 7)} ảnh bổ sung.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã lưu biên bản, ảnh mặt trực tiếp và kết quả đối chiếu người nhận. Hãy in, ký và tải bản ký để Staff kiểm tra trước khi bắt đầu chuyến.";
        return RedirectToBookingDetails(model.BookingId);
    }

    [HttpGet]
    public IActionResult Print(int bookingId) =>
        RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId });

    private async Task<bool> PopulateVerifiedIdentityAsync(
        HandoverViewModel model,
        BookingDetailsDto booking,
        CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == booking.CustomerId && user.IsActive)
            .Select(user => new { user.Id, user.FullName })
            .FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            return false;
        }

        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == booking.CustomerId &&
                document.Status == DocumentStatus.Verified &&
                (document.DocumentType == DocumentTypes.CitizenId ||
                 document.DocumentType == DocumentTypes.DrivingLicense))
            .ToListAsync(cancellationToken);

        var citizen = documents.FirstOrDefault(document => document.DocumentType == DocumentTypes.CitizenId);
        var license = documents.FirstOrDefault(document => document.DocumentType == DocumentTypes.DrivingLicense);
        if (citizen is null || license is null ||
            !citizen.ExpiryDate.HasValue || citizen.ExpiryDate.Value.Date < booking.ReturnDate.Date ||
            !license.ExpiryDate.HasValue || license.ExpiryDate.Value.Date < booking.ReturnDate.Date)
        {
            return false;
        }

        model.CustomerId = customer.Id;
        model.VerifiedCustomerName = customer.FullName;
        model.VerifiedCitizenId = citizen.DocumentNumber;
        model.VerifiedDrivingLicenseNumber = license.DocumentNumber;
        return true;
    }

    private async Task ValidateIdentityFaceSessionAsync(
        HandoverViewModel model,
        BookingDetailsDto booking,
        ICollection<string> identityFailures,
        CancellationToken cancellationToken)
    {
        if (!model.IdentityFaceSessionId.HasValue)
        {
            identityFailures.Add("chưa chụp ảnh mặt người nhận trực tiếp");
            return;
        }

        var valid = await _dbContext.Set<IdentityCaptureSession>()
            .AsNoTracking()
            .AnyAsync(session =>
                session.IdentityCaptureSessionId == model.IdentityFaceSessionId.Value &&
                session.Purpose == IdentityCapturePurposes.Handover &&
                session.BookingId == booking.BookingId &&
                session.TargetCustomerId == booking.CustomerId &&
                session.CompletedAt.HasValue &&
                !session.ConsumedAt.HasValue &&
                session.ImagePath != null,
                cancellationToken);

        if (!valid)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.IdentityFaceSessionId),
                "Ảnh mặt người nhận không hợp lệ, không thuộc đúng đơn/khách hoặc đã được sử dụng. Vui lòng chụp lại.");
            identityFailures.Add("ảnh mặt trực tiếp không hợp lệ");
        }
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
        var required = new IFormFile?[]
        {
            model.FrontImage,
            model.RearImage,
            model.LeftImage,
            model.RightImage,
            model.InteriorImage,
            model.OdometerImage,
            model.FuelImage
        };

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

        var duplicateCandidates = new (string FieldName, IFormFile? File)[]
        {
            (nameof(HandoverViewModel.FrontImage), model.FrontImage),
            (nameof(HandoverViewModel.RearImage), model.RearImage),
            (nameof(HandoverViewModel.LeftImage), model.LeftImage),
            (nameof(HandoverViewModel.RightImage), model.RightImage),
            (nameof(HandoverViewModel.InteriorImage), model.InteriorImage),
            (nameof(HandoverViewModel.OdometerImage), model.OdometerImage),
            (nameof(HandoverViewModel.FuelImage), model.FuelImage)
        }.Concat(additional.Select(file => (nameof(HandoverViewModel.Images), (IFormFile?)file)));

        var duplicatesByField = await ImageFileValidator.FindDuplicateContentFieldsAsync(
            duplicateCandidates,
            cancellationToken);
        foreach (var duplicate in duplicatesByField)
        {
            ModelState.AddModelError(
                duplicate.Key,
                "Không được dùng cùng một ảnh cho nhiều vị trí. Ảnh trùng nội dung: " +
                string.Join(", ", duplicate.Value));
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

