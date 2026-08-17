using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminExtensionsController : Controller
{
    private const long MaximumEvidenceImageBytes = 5 * 1024 * 1024;

    private readonly IExtensionService _extensionService;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;

    public AdminExtensionsController(
        IExtensionService extensionService,
        IBookingService bookingService,
        IAuditService auditService,
        IWebHostEnvironment environment)
    {
        _extensionService = extensionService;
        _bookingService = bookingService;
        _auditService = auditService;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _extensionService.GetPendingExtensionsAsync(cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateForCustomer(
        int bookingId,
        DateTime requestedReturnDate,
        string? note,
        bool isForceMajeure,
        string? evidenceNote,
        IFormFile? evidenceImage,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] = "Chỉ đơn đang thuê mới được ghi nhận yêu cầu gia hạn.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (requestedReturnDate <= booking.ReturnDate)
        {
            TempData["ErrorMessage"] = "Giờ trả mới phải sau giờ trả hiện tại.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var (imagePath, imageError) = await SaveEvidenceImageAsync(
            bookingId,
            evidenceImage,
            cancellationToken);

        if (imageError is not null)
        {
            TempData["ErrorMessage"] = imageError;
            return RedirectToAction(nameof(Index));
        }

        var phoneNote = string.IsNullOrWhiteSpace(note)
            ? "SmartCar ghi nhận yêu cầu qua điện thoại."
            : $"SmartCar ghi nhận yêu cầu qua điện thoại. {note.Trim()}";

        var result = await _extensionService.RequestAsync(
            booking.CustomerId,
            new RequestExtensionRequest(
                bookingId,
                requestedReturnDate,
                phoneNote,
                isForceMajeure,
                ComposeEvidence(evidenceNote, imagePath)),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedEvidenceImage(imagePath);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? isForceMajeure
                ? "Đã ghi nhận yêu cầu bất khả kháng qua điện thoại; yêu cầu đang chờ kiểm tra minh chứng/lịch xe."
                : "Đã ghi nhận yêu cầu gia hạn qua điện thoại."
            : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            await _auditService.WriteAsync(
                adminId,
                "CreateExtensionForCustomer",
                nameof(BookingExtension),
                bookingId.ToString(),
                $"Ghi nhận yêu cầu gia hạn qua điện thoại cho đơn #{bookingId} đến {requestedReturnDate:dd/MM/yyyy HH:mm}. Loại: {(isForceMajeure ? "bất khả kháng" : "thông thường")}.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);
        }

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(
        int id,
        bool confirmConflictHandled,
        string? adminNote,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _extensionService.ApproveAsync(
            id,
            adminId,
            confirmConflictHandled,
            adminNote,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã duyệt gia hạn và tạo khoản thanh toán. Ngày trả mới chỉ có hiệu lực sau khi xác nhận thanh toán."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEvidence(
        int extensionId,
        string reason,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _extensionService.RequestMoreEvidenceAsync(
            extensionId,
            adminId,
            reason,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã yêu cầu khách bổ sung minh chứng."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        RejectExtensionViewModel model,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = ModelState.IsValid
            ? await _extensionService.RejectAsync(
                model.ExtensionId,
                adminId,
                model.Reason,
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Vui lòng nhập lý do từ chối.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã từ chối yêu cầu gia hạn và gửi lý do cho khách."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    private async Task<(string? Path, string? Error)> SaveEvidenceImageAsync(
        int bookingId,
        IFormFile? image,
        CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
        {
            return (null, null);
        }

        var validationError = await ImageFileValidator.ValidateAsync(
            image,
            MaximumEvidenceImageBytes,
            cancellationToken);

        if (validationError is not null)
        {
            return (null, $"Ảnh minh chứng: {validationError}");
        }

        var relativeFolder = $"uploads/extensions/{bookingId}";
        var physicalFolder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(physicalFolder);

        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var physicalPath = Path.Combine(physicalFolder, fileName);

        await using var stream = new FileStream(
            physicalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        await image.CopyToAsync(stream, cancellationToken);

        return ($"/{relativeFolder}/{fileName}", null);
    }

    private void DeleteSavedEvidenceImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) ||
            !imagePath.StartsWith("/uploads/extensions/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relative = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var physicalPath = Path.Combine(_environment.WebRootPath, relative);
        if (System.IO.File.Exists(physicalPath))
        {
            System.IO.File.Delete(physicalPath);
        }
    }

    private static string ComposeEvidence(string? evidenceNote, string? imagePath)
    {
        var text = string.IsNullOrWhiteSpace(evidenceNote)
            ? string.Empty
            : string.Join(
                " | ",
                evidenceNote
                    .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0));

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            text = string.IsNullOrWhiteSpace(text)
                ? $"Ảnh minh chứng: {imagePath}"
                : $"{text} | Ảnh minh chứng: {imagePath}";
        }

        return text;
    }
}
