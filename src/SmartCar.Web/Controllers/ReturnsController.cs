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
    private const int MinimumImages = 6;
    private const int MaximumImages = 15;
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

        var preparation = await _returnService.GetPreparationAsync(bookingId, cancellationToken);
        if (preparation is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy dữ liệu bàn giao để đối chiếu khi nhận lại xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        PopulateReturnContext(preparation, DateTime.Now, null, null);

        return View(new ReturnViewModel
        {
            BookingId = bookingId,
            ReturnedAt = DateTime.Now,
            ReturnLocation = preparation.ScheduledReturnLocation,
            Mileage = preparation.HandoverMileage
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ReturnViewModel model,
        string? accessoryStatus,
        string? accessoryNote,
        CancellationToken cancellationToken)
    {
        var preparation = await _returnService.GetPreparationAsync(model.BookingId, cancellationToken);
        if (preparation is null)
        {
            ModelState.AddModelError(string.Empty, "Không tìm thấy dữ liệu bàn giao để đối chiếu khi nhận lại xe.");
            return View(model);
        }

        NormalizeConditionSummary(model);
        ValidateAccessoryStatus(accessoryStatus, accessoryNote);
        await ValidateImagesAsync(model.Images, cancellationToken);

        if (!ModelState.IsValid)
        {
            PopulateReturnContext(preparation, model.ReturnedAt, accessoryStatus, accessoryNote);
            return View(model);
        }

        var imagePaths = await SaveImagesAsync(
            model.BookingId,
            model.Images,
            cancellationToken);

        var notes = ComposeReturnNotes(model.Notes, accessoryStatus!, accessoryNote);
        var result = await _returnService.CreateAsync(
            new CreateReturnRequest(
                model.BookingId,
                model.ReturnedAt,
                model.ReturnLocation,
                model.Mileage,
                model.FuelLevel,
                model.ExteriorCondition,
                model.InteriorCondition,
                model.HasDamage,
                string.Join(';', imagePaths),
                notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedImages(imagePaths);
            AddErrors(result.Errors);
            PopulateReturnContext(preparation, model.ReturnedAt, accessoryStatus, accessoryNote);
            return View(model);
        }

        var travelledKm = Math.Max(0, model.Mileage - preparation.HandoverMileage);
        await WriteAuditAsync(
            "CreateReturn",
            nameof(VehicleReturn),
            model.BookingId,
            $"Tiếp nhận xe trả đơn #{model.BookingId}; địa điểm {model.ReturnLocation}; ODO giao {preparation.HandoverMileage:N0} km, ODO trả {model.Mileage:N0} km, quãng đường sử dụng {travelledKm:N0} km; nhiên liệu/pin giao {preparation.HandoverFuelLevel}, trả {model.FuelLevel}; phụ kiện: {accessoryStatus}; {imagePaths.Count} ảnh; bất thường mới: {(model.HasDamage ? "Có" : "Không")}.",
            cancellationToken);

        TempData["SuccessMessage"] =
            "Đã tiếp nhận xe và lưu biên bản đối chiếu. Xe chuyển sang Chờ kiểm tra để xử lý phụ phí hoặc bảo trì nếu có.";
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

    private void PopulateReturnContext(
        ReturnPreparationDto preparation,
        DateTime returnedAt,
        string? accessoryStatus,
        string? accessoryNote)
    {
        ViewBag.ScheduledReturnDate = preparation.ScheduledReturnDate;
        ViewBag.IsEarlyReturn = returnedAt < preparation.ScheduledReturnDate;
        ViewBag.HandoverAt = preparation.HandoverAt;
        ViewBag.HandoverMileage = preparation.HandoverMileage;
        ViewBag.HandoverFuelLevel = preparation.HandoverFuelLevel;
        ViewBag.HandoverAccessories = preparation.HandoverAccessories;
        ViewBag.HandoverImagePaths = preparation.HandoverImagePaths;
        ViewBag.VehicleFuelType = preparation.VehicleFuelType;
        ViewBag.AccessoryStatus = accessoryStatus ?? "Complete";
        ViewBag.AccessoryNote = accessoryNote ?? string.Empty;
    }

    private void NormalizeConditionSummary(ReturnViewModel model)
    {
        var exterior = model.ExteriorCondition?.Trim();

        if (model.HasDamage ||
            (!string.IsNullOrWhiteSpace(exterior) &&
             exterior.StartsWith("Có bất thường mới:", StringComparison.OrdinalIgnoreCase)))
        {
            var detail = exterior?.StartsWith("Có bất thường mới:", StringComparison.OrdinalIgnoreCase) == true
                ? exterior["Có bất thường mới:".Length..].Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(detail))
            {
                ModelState.AddModelError(nameof(ReturnViewModel.ExteriorCondition),
                    "Vui lòng mô tả bất thường hoặc hư hỏng mới phát hiện khi nhận lại xe.");
                return;
            }

            model.HasDamage = true;
            model.ExteriorCondition = $"Có bất thường mới: {detail}";
            model.InteriorCondition =
                "Tình trạng nội thất được đối chiếu bằng bộ ảnh trả xe; bất thường nếu có được ghi trong tình trạng chung.";
            return;
        }

        model.HasDamage = false;
        model.ExteriorCondition = "Không phát hiện bất thường mới khi nhận lại xe.";
        model.InteriorCondition = "Không phát hiện bất thường mới khi nhận lại xe.";
    }

    private void ValidateAccessoryStatus(string? accessoryStatus, string? accessoryNote)
    {
        if (accessoryStatus is not ("Complete" or "Issue"))
        {
            ModelState.AddModelError(string.Empty, "Vui lòng xác nhận tình trạng phụ kiện khi nhận lại xe.");
            return;
        }

        if (accessoryStatus == "Issue" && string.IsNullOrWhiteSpace(accessoryNote))
        {
            ModelState.AddModelError(string.Empty,
                "Vui lòng mô tả phụ kiện bị thiếu hoặc hư hỏng khi nhận lại xe.");
        }
    }

    private static string ComposeReturnNotes(
        string? notes,
        string accessoryStatus,
        string? accessoryNote)
    {
        var accessoryText = accessoryStatus == "Complete"
            ? "Phụ kiện khi nhận lại: đầy đủ theo biên bản giao xe."
            : $"Phụ kiện khi nhận lại có vấn đề: {accessoryNote?.Trim()}";

        return string.IsNullOrWhiteSpace(notes)
            ? accessoryText
            : $"{accessoryText}\nGhi chú bổ sung: {notes.Trim()}";
    }

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images.Where(file => file.Length > 0).ToList();
        if (selectedImages.Count < MinimumImages)
        {
            ModelState.AddModelError(nameof(ReturnViewModel.Images),
                $"Vui lòng tải tối thiểu {MinimumImages} ảnh đối chiếu khi nhận lại xe: trước xe, sau xe, hai bên thân xe, đồng hồ ODO/nhiên liệu và nội thất.");
            return;
        }

        if (selectedImages.Count > MaximumImages)
        {
            ModelState.AddModelError(nameof(ReturnViewModel.Images),
                $"Chỉ được tải tối đa {MaximumImages} ảnh khi nhận lại xe.");
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
