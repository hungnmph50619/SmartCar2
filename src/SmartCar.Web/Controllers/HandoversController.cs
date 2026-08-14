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
    private const int MinimumImages = 6;
    private const int MaximumImages = 15;
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
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang sẵn sàng giao xe mới được lập biên bản bàn giao.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View(new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = DateTime.Now > booking.PickupDate
                ? DateTime.Now
                : booking.PickupDate,
            ExteriorCondition = "Không phát hiện bất thường tại thời điểm bàn giao.",
            InteriorCondition = "Không phát hiện bất thường tại thời điểm bàn giao."
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        NormalizeConditionSummary(model);
        await ValidateImagesAsync(model.Images, cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var imagePaths = await SaveImagesAsync(
            model.BookingId,
            model.Images,
            cancellationToken);

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
            $"Lập biên bản giao xe cho đơn #{model.BookingId}, số km {model.Mileage}, {imagePaths.Count} ảnh đối chiếu, tình trạng: {model.ExteriorCondition}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã lập biên bản giao xe và lưu bộ ảnh đối chiếu.";
        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    private void NormalizeConditionSummary(HandoverViewModel model)
    {
        var exterior = model.ExteriorCondition?.Trim();

        if (string.IsNullOrWhiteSpace(exterior))
        {
            model.ExteriorCondition = "Không phát hiện bất thường tại thời điểm bàn giao.";
            model.InteriorCondition = "Không phát hiện bất thường tại thời điểm bàn giao.";
            return;
        }

        if (exterior.StartsWith("Có bất thường:", StringComparison.OrdinalIgnoreCase))
        {
            var detail = exterior["Có bất thường:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                ModelState.AddModelError(nameof(HandoverViewModel.ExteriorCondition),
                    "Vui lòng mô tả bất thường đã phát hiện trên xe.");
                return;
            }

            model.ExteriorCondition = $"Có bất thường: {detail}";
            model.InteriorCondition = "Tình trạng nội thất được đối chiếu bằng bộ ảnh bàn giao; bất thường nếu có được ghi trong tình trạng chung.";
            return;
        }

        model.ExteriorCondition = "Không phát hiện bất thường tại thời điểm bàn giao.";
        model.InteriorCondition = "Không phát hiện bất thường tại thời điểm bàn giao.";
    }

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images.Where(file => file.Length > 0).ToList();
        if (selectedImages.Count < MinimumImages)
        {
            ModelState.AddModelError(nameof(HandoverViewModel.Images),
                $"Vui lòng tải tối thiểu {MinimumImages} ảnh đối chiếu khi bàn giao: trước xe, sau xe, hai bên thân xe, đồng hồ km/nhiên liệu và nội thất.");
            return;
        }

        if (selectedImages.Count > MaximumImages)
        {
            ModelState.AddModelError(nameof(HandoverViewModel.Images),
                $"Chỉ được tải tối đa {MaximumImages} ảnh bàn giao.");
        }

        foreach (var image in selectedImages)
        {
            var error = await ImageFileValidator.ValidateAsync(
                image,
                MaximumImageBytes,
                cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(HandoverViewModel.Images),
                    $"{image.FileName}: {error}");
            }
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

        var paths = new List<string>();
        foreach (var image in images.Where(file => file.Length > 0))
        {
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(folder, fileName);

            await using var stream = System.IO.File.Create(fullPath);
            await image.CopyToAsync(stream, cancellationToken);
            paths.Add($"/{relativeFolder}/{fileName}");
        }

        return paths;
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
