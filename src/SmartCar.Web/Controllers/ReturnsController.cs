using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Returns;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReturnsController : Controller
{
    private const int MinimumVehicleImages = 6;
    private const int MaximumVehicleImages = 15;
    private const int MinimumDocumentImages = 1;
    private const int MaximumDocumentImages = 3;
    private const long MaximumImageBytes = 5 * 1024 * 1024;
    private const string SignatureRefusedAuditAction = "ReturnDocumentSignatureRefused";
    private const string SignatureResolvedAuditAction = "ReturnDocumentSignatureDisputeResolved";

    private readonly IReturnService _returnService;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;
    private readonly ApplicationDbContext _dbContext;

    public ReturnsController(
        IReturnService returnService,
        IBookingService bookingService,
        IAuditService auditService,
        IWebHostEnvironment environment,
        ApplicationDbContext dbContext)
    {
        _returnService = returnService;
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

        PopulateReturnContext(preparation, DateTime.Now);

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
        CancellationToken cancellationToken)
    {
        var preparation = await _returnService.GetPreparationAsync(model.BookingId, cancellationToken);
        if (preparation is null)
        {
            ModelState.AddModelError(string.Empty, "Không tìm thấy dữ liệu bàn giao để đối chiếu khi nhận lại xe.");
            return View(model);
        }

        NormalizeConditionSummary(model);

        var documentStatus = model.ReturnDocumentStatus?.Trim() ?? string.Empty;
        var customerSigned = string.Equals(documentStatus, "Signed", StringComparison.Ordinal);
        var customerRefused = string.Equals(documentStatus, "Refused", StringComparison.Ordinal);

        if (!customerSigned && !customerRefused)
        {
            ModelState.AddModelError(
                nameof(ReturnViewModel.ReturnDocumentStatus),
                "Vui lòng chọn khách đã ký biên bản hoặc khách từ chối ký/có tranh chấp.");
        }

        if (customerRefused)
        {
            var refusalReason = model.SignatureRefusalReason?.Trim() ?? string.Empty;
            if (refusalReason.Length < 5)
            {
                ModelState.AddModelError(
                    nameof(ReturnViewModel.SignatureRefusalReason),
                    "Khi khách từ chối ký, vui lòng ghi rõ lý do hoặc diễn biến, tối thiểu 5 ký tự.");
            }
            else
            {
                model.SignatureRefusalReason = refusalReason;
            }
        }
        else
        {
            model.SignatureRefusalReason = null;
        }

        await ValidateImageCollectionAsync(
            model.Images,
            MinimumVehicleImages,
            MaximumVehicleImages,
            nameof(ReturnViewModel.Images),
            "ảnh hiện trạng xe khi nhận lại",
            cancellationToken);

        await ValidateImageCollectionAsync(
            model.SignedDocumentImages,
            MinimumDocumentImages,
            MaximumDocumentImages,
            nameof(ReturnViewModel.SignedDocumentImages),
            customerRefused
                ? "ảnh biên bản/hồ sơ ghi nhận khách từ chối ký"
                : "ảnh biên bản trả xe đã có chữ ký khách",
            cancellationToken);

        if (!ModelState.IsValid)
        {
            PopulateReturnContext(preparation, model.ReturnedAt);
            return View(model);
        }

        var vehicleImagePaths = await SaveImagesAsync(
            model.BookingId,
            model.Images,
            "uploads/returns",
            "vehicle",
            cancellationToken);
        var documentImagePaths = await SaveImagesAsync(
            model.BookingId,
            model.SignedDocumentImages,
            "uploads/return-documents",
            customerRefused ? "refusal-document" : "signed-document",
            cancellationToken);
        var allEvidencePaths = vehicleImagePaths.Concat(documentImagePaths).ToList();

        var returnNote = customerRefused
            ? $"Khách từ chối ký biên bản trả xe. Lý do/diễn biến: {model.SignatureRefusalReason}"
            : null;

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
                string.Join(';', allEvidencePaths),
                returnNote),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedImages(allEvidencePaths);
            AddErrors(result.Errors);
            PopulateReturnContext(preparation, model.ReturnedAt);
            return View(model);
        }

        var travelledKm = Math.Max(0, model.Mileage - preparation.HandoverMileage);
        await WriteAuditAsync(
            "CreateReturn",
            nameof(VehicleReturn),
            model.BookingId,
            $"Tiếp nhận xe trả đơn #{model.BookingId}; địa điểm {model.ReturnLocation}; " +
            $"số km khi giao {preparation.HandoverMileage:N0} km, số km khi trả {model.Mileage:N0} km, quãng đường sử dụng {travelledKm:N0} km; " +
            $"nhiên liệu/pin khi giao {preparation.HandoverFuelLevel}, khi trả {model.FuelLevel}; " +
            $"{vehicleImagePaths.Count} ảnh hiện trạng xe, {documentImagePaths.Count} ảnh hồ sơ biên bản trả xe; " +
            $"trạng thái biên bản: {(customerRefused ? "Khách từ chối ký/có tranh chấp" : "Khách đã ký")}; " +
            $"hư hỏng mới ghi nhận: {(model.HasDamage ? "Có" : "Không")}.",
            cancellationToken);

        if (customerRefused)
        {
            await WriteAuditAsync(
                SignatureRefusedAuditAction,
                nameof(Booking),
                model.BookingId,
                $"Khách từ chối ký biên bản trả xe. Lý do/diễn biến: {model.SignatureRefusalReason}. " +
                $"SmartCar đã lưu {vehicleImagePaths.Count} ảnh hiện trạng và {documentImagePaths.Count} ảnh biên bản/hồ sơ ghi nhận từ chối ký. " +
                "Khóa quyết toán cọc cho đến khi Admin xử lý tranh chấp.",
                cancellationToken);

            await UpdateReturnReceiptNotificationAsync(
                model.BookingId,
                cancellationToken);

            TempData["SuccessMessage"] =
                "Đã nhận lại xe và ghi nhận khách từ chối ký biên bản. Cọc đang bị khóa quyết toán cho đến khi Admin xử lý hồ sơ tranh chấp.";
            return RedirectToAction(
                "Review",
                "ReturnEvidence",
                new { bookingId = model.BookingId });
        }

        await WriteAuditAsync(
            "ReturnDocumentSigned",
            nameof(Booking),
            model.BookingId,
            $"Khách đã ký biên bản trả xe; SmartCar lưu {documentImagePaths.Count} ảnh biên bản có chữ ký cùng hồ sơ đơn.",
            cancellationToken);

        TempData["SuccessMessage"] =
            "Đã tiếp nhận xe và lưu biên bản trả xe có chữ ký. Xe chuyển sang Chờ kiểm tra để xử lý phụ phí và quyết toán cọc nếu có.";
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
        if (await HasOpenReturnSignatureDisputeAsync(model.BookingId, cancellationToken))
        {
            TempData["ErrorMessage"] =
                "Khách đang từ chối ký biên bản trả xe. Phải mở hồ sơ giao nhận, xử lý tranh chấp và ghi rõ căn cứ trước khi quyết toán cọc hoặc hoàn tất đơn.";
            return RedirectToAction(
                "Review",
                "ReturnEvidence",
                new { bookingId = model.BookingId });
        }

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
        DateTime returnedAt)
    {
        ViewBag.ScheduledReturnDate = preparation.ScheduledReturnDate;
        ViewBag.IsEarlyReturn = returnedAt < preparation.ScheduledReturnDate;
        ViewBag.HandoverAt = preparation.HandoverAt;
        ViewBag.HandoverMileage = preparation.HandoverMileage;
        ViewBag.HandoverFuelLevel = preparation.HandoverFuelLevel;
        ViewBag.HandoverVehicleImagePaths = preparation.HandoverVehicleImagePaths;
        ViewBag.HandoverDocumentImagePaths = preparation.HandoverDocumentImagePaths;
        ViewBag.VehicleFuelType = preparation.VehicleFuelType;
    }

    private static void NormalizeConditionSummary(ReturnViewModel model)
    {
        var detail = model.ExteriorCondition?.Trim();
        model.HasDamage = !string.IsNullOrWhiteSpace(detail);
        model.ExteriorCondition = model.HasDamage
            ? $"Hư hỏng/bất thường mới: {detail}"
            : null;
        model.InteriorCondition = null;
    }

    private async Task<bool> HasOpenReturnSignatureDisputeAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var bookingKey = bookingId.ToString();
        var latestRefusalAt = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == SignatureRefusedAuditAction &&
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingKey)
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => (DateTime?)log.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (!latestRefusalAt.HasValue)
        {
            return false;
        }

        var latestResolutionAt = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == SignatureResolvedAuditAction &&
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingKey)
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => (DateTime?)log.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return !latestResolutionAt.HasValue ||
               latestResolutionAt.Value < latestRefusalAt.Value;
    }

    private async Task UpdateReturnReceiptNotificationAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var customerId = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.BookingId == bookingId)
            .Select(booking => booking.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return;
        }

        var notification = await _dbContext.Notifications
            .Where(item =>
                item.UserId == customerId &&
                (item.Title == "Đã tiếp nhận xe trả" ||
                 item.Title == "Đã tiếp nhận xe trả sớm"))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (notification is null)
        {
            return;
        }

        notification.Message =
            $"Đơn #{bookingId} đã được SmartCar tiếp nhận xe. Bạn đã từ chối ký biên bản trả xe; SmartCar đã lưu ảnh hiện trạng và hồ sơ ghi nhận việc từ chối ký. " +
            "Hồ sơ đang chờ Admin xử lý tranh chấp và cọc chưa được quyết toán.";

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateImageCollectionAsync(
        IReadOnlyCollection<IFormFile> images,
        int minimum,
        int maximum,
        string modelKey,
        string description,
        CancellationToken cancellationToken)
    {
        var selected = images.Where(file => file.Length > 0).ToList();
        if (selected.Count < minimum)
        {
            ModelState.AddModelError(modelKey,
                $"Vui lòng tải tối thiểu {minimum} {description}.");
            return;
        }

        if (selected.Count > maximum)
        {
            ModelState.AddModelError(modelKey,
                $"Chỉ được tải tối đa {maximum} {description}.");
        }

        foreach (var image in selected)
        {
            var error = await ImageFileValidator.ValidateAsync(
                image,
                MaximumImageBytes,
                cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(modelKey, $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        string rootFolder,
        string filePrefix,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"{rootFolder}/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var paths = new List<string>();
        var index = 0;
        foreach (var image in images.Where(file => file.Length > 0))
        {
            index++;
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"{filePrefix}-{index:00}-{Guid.NewGuid():N}{extension}";
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
