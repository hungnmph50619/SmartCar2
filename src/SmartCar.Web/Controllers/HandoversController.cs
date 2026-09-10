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

[Authorize(Roles = RoleNames.Admin + "," + RoleNames.Staff)]
public sealed class HandoversController : Controller
{
    private const int MinimumImages = 7;
    private const int MaximumImages = 25;
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
            return RedirectToBookingDetails(bookingId);
        }

        if (booking.HasHandover)
        {
            TempData["ErrorMessage"] =
                "Biên bản điện tử đã được lập. Hãy in, ký và tải bản ký.";
            return RedirectToBookingDetails(bookingId);
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao.";
            return RedirectToBookingDetails(bookingId);
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
            DamageCompensationTerms = RentalPolicy.DamageCompensationTerms,
            PenaltyPolicyAccepted = true
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
            return RedirectToBookingDetails(model.BookingId);
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao.";
            return RedirectToBookingDetails(model.BookingId);
        }

        // Các mức phí luôn lấy từ server, không nhận giá trị chính sách do browser gửi lên.
        ModelState.Remove(nameof(HandoverViewModel.IncludedKilometers));
        ModelState.Remove(nameof(HandoverViewModel.ExcessKmFeePerKm));
        ModelState.Remove(nameof(HandoverViewModel.LateReturnFeeMultiplier));
        ModelState.Remove(nameof(HandoverViewModel.TrafficFineTerms));
        ModelState.Remove(nameof(HandoverViewModel.DamageCompensationTerms));
        ModelState.Remove(nameof(HandoverViewModel.PenaltyPolicyAccepted));
        // Danh sách file là non-nullable nên MVC có thể sinh lỗi Required mặc định bằng tiếng Anh.
        // Bỏ lỗi mặc định và dùng toàn bộ validation ảnh tiếng Việt ở ValidateImagesAsync bên dưới.
        ModelState.Remove(nameof(HandoverViewModel.Images));

        model.IncludedKilometers = booking.NumberOfDays * RentalPolicy.IncludedKilometersPerDay;
        model.ExcessKmFeePerKm = RentalPolicy.ExcessKilometerFee;
        model.LateReturnFeeMultiplier = RentalPolicy.LateReturnFeeMultiplier;
        model.TrafficFineTerms = RentalPolicy.TrafficFineTerms;
        model.DamageCompensationTerms = RentalPolicy.DamageCompensationTerms;
        // Việc lưu biên bản là hành động xác nhận chính sách đã được thông báo trong quy trình giao xe.
        model.PenaltyPolicyAccepted = true;

        var identityMatches = await CustomerCitizenIdMatchesAsync(
            booking.CustomerId,
            model.ReceiverCitizenId,
            cancellationToken);

        if (!identityMatches)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.ReceiverCitizenId),
                "CCCD người nhận không trùng với CCCD đã xác minh của khách đứng tên đơn thuê.");
        }

        await ValidateImagesAsync(model.Images, cancellationToken);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        IReadOnlyList<string> imagePaths;
        try
        {
            imagePaths = await SaveImagesAsync(
                model.BookingId,
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

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var handover = await _dbContext.VehicleHandovers
            .FirstAsync(item => item.BookingId == model.BookingId, cancellationToken);
        handover.CustomerIdentityVerified = true;
        handover.IdentityVerifiedByStaffId = staffId;
        handover.IdentityVerifiedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            staffId,
            "CreateHandover",
            nameof(VehicleHandover),
            model.BookingId.ToString(),
            $"Lập biên bản giao điện tử đơn #{model.BookingId}, {mileage:N0} km, {imagePaths.Count} ảnh chứng cứ.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã lưu biên bản điện tử và xác minh đúng người nhận. Hãy in, ký và tải bản ký.";

        return RedirectToBookingDetails(model.BookingId);
    }

    // Giữ URL cũ để các liên kết cũ vẫn hoạt động.
    [HttpGet]
    public IActionResult Print(int bookingId) =>
        RedirectToAction(
            "HandoverPrint",
            "AdminRentalDocuments",
            new { bookingId });

    private async Task<bool> CustomerCitizenIdMatchesAsync(
        string customerId,
        string? enteredCitizenId,
        CancellationToken cancellationToken)
    {
        var normalized = new string((enteredCitizenId ?? string.Empty)
            .Where(char.IsDigit)
            .ToArray());

        if (normalized.Length != 12)
        {
            return false;
        }

        return await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                document.CustomerId == customerId &&
                document.DocumentType == DocumentTypes.CitizenId &&
                document.Status == DocumentStatus.Verified &&
                document.DocumentNumber == normalized,
                cancellationToken);
    }

    private IActionResult RedirectToBookingDetails(int bookingId) =>
        User.IsInRole(RoleNames.Staff)
            ? RedirectToAction("Details", "Staff", new { id = bookingId })
            : RedirectToAction("Details", "AdminBookings", new { id = bookingId });

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images
            .Where(file => file.Length > 0)
            .ToList();

        if (selectedImages.Count < MinimumImages)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                $"Vui lòng chọn ít nhất {MinimumImages} ảnh bàn giao.");
        }

        if (selectedImages.Count > MaximumImages)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                $"Vui lòng chọn tối đa {MaximumImages} ảnh bàn giao.");
        }

        foreach (var image in selectedImages)
        {
            await ValidateImageAsync(
                image,
                nameof(HandoverViewModel.Images),
                cancellationToken);
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
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/handovers/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var selectedImages = images
            .Where(file => file.Length > 0)
            .ToList();

        var paths = new List<string>();
        try
        {
            for (var index = 0; index < selectedImages.Count; index++)
            {
                paths.Add(await SaveOneImageAsync(
                    folder,
                    relativeFolder,
                    $"evidence-{index + 1:00}",
                    selectedImages[index],
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