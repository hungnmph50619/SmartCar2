using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Returns;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReturnsController : Controller
{
    private const int MaximumImages = 10;
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private readonly IReturnService _returnService;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;

    public ReturnsController(
        IReturnService returnService,
        IBookingService bookingService,
        IAuditService auditService,
        IWebHostEnvironment environment)
    {
        _returnService = returnService;
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

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang thuê và đã bàn giao xe mới được lập biên bản trả xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View(new ReturnViewModel
        {
            BookingId = bookingId,
            ReturnedAt = DateTime.Now > booking.ReturnDate
                ? DateTime.Now
                : booking.ReturnDate
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ReturnViewModel model,
        CancellationToken cancellationToken)
    {
        await ValidateImagesAsync(model.Images, cancellationToken);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var imagePaths = await SaveImagesAsync(
            model.BookingId,
            model.Images,
            cancellationToken);

        var result = await _returnService.CreateAsync(
            new CreateReturnRequest(
                model.BookingId,
                model.ReturnedAt,
                model.Mileage,
                model.FuelLevel,
                model.ExteriorCondition,
                model.InteriorCondition,
                model.HasDamage,
                string.Join(';', imagePaths),
                model.Notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedImages(imagePaths);
            AddErrors(result.Errors);
            return View(model);
        }

        await WriteAuditAsync(
            "CreateReturn",
            nameof(VehicleReturn),
            model.BookingId,
            $"Lập biên bản trả xe cho đơn #{model.BookingId}, số km {model.Mileage}, {imagePaths.Count} ảnh, có hư hỏng mới: {(model.HasDamage ? "Có" : "Không")}.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã lập biên bản trả xe và lưu ảnh tình trạng xe. Xe chuyển sang chờ kiểm tra.";
        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCharge(
        AddChargeViewModel model,
        CancellationToken cancellationToken)
    {
        var result = ModelState.IsValid
            ? await _returnService.AddChargeAsync(
                new AddChargeRequest(
                    model.BookingId,
                    model.ChargeType,
                    model.Description,
                    model.Amount),
                cancellationToken)
            : OperationResult.Failure("Thông tin phụ phí không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã thêm phụ phí và cập nhật tổng tiền."
            : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "AddCharge",
                nameof(AdditionalCharge),
                model.BookingId,
                $"Thêm phụ phí {model.ChargeType} cho đơn #{model.BookingId}: {model.Amount:N0} đồng. {model.Description}",
                cancellationToken);
        }

        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCharge(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken)
    {
        var result = await _returnService.RemoveChargeAsync(
            bookingId,
            additionalChargeId,
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xóa phụ phí."
            : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "RemoveCharge",
                nameof(AdditionalCharge),
                additionalChargeId,
                $"Xóa phụ phí #{additionalChargeId} khỏi đơn #{bookingId}.",
                cancellationToken);
        }

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(
        CompleteBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await _returnService.CompleteAsync(
            model.BookingId,
            model.RequiresMaintenance,
            model.MaintenanceNote,
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã hoàn tất đơn và cập nhật trạng thái xe."
            : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "CompleteBooking",
                nameof(Booking),
                model.BookingId,
                $"Hoàn tất đơn #{model.BookingId}. Yêu cầu bảo trì: {(model.RequiresMaintenance ? "Có" : "Không")}.",
                cancellationToken);
        }

        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images.Where(file => file.Length > 0).ToList();
        if (selectedImages.Count == 0)
        {
            ModelState.AddModelError(nameof(ReturnViewModel.Images),
                "Vui lòng tải ít nhất một ảnh tình trạng xe khi trả.");
            return;
        }

        if (selectedImages.Count > MaximumImages)
        {
            ModelState.AddModelError(nameof(ReturnViewModel.Images),
                $"Chỉ được tải tối đa {MaximumImages} ảnh khi trả xe.");
        }

        foreach (var image in selectedImages)
        {
            var error = await ImageFileValidator.ValidateAsync(
                image,
                MaximumImageBytes,
                cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(ReturnViewModel.Images),
                    $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/returns/{bookingId}";
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

    private Task WriteAuditAsync(
        string action,
        string entityName,
        int entityId,
        string description,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return _auditService.WriteAsync(
            adminId,
            action,
            entityName,
            entityId.ToString(),
            description,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }

    private void AddErrors(IEnumerable<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }
}
