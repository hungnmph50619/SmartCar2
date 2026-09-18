using System.Globalization;
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

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminExtensionRequestsController : Controller
{
    private const long MaximumEvidenceImageBytes = 5 * 1024 * 1024;
    private const int MinimumEvidenceImageCount = 2;
    private const int MaximumEvidenceImageCount = 8;

    private readonly IBookingService _bookingService;
    private readonly IExtensionService _extensionService;
    private readonly IAuditService _auditService;
    private readonly ISecureDocumentStorage _secureDocumentStorage;

    public AdminExtensionRequestsController(
        IBookingService bookingService,
        IExtensionService extensionService,
        IAuditService auditService,
        ISecureDocumentStorage secureDocumentStorage)
    {
        _bookingService = bookingService;
        _extensionService = extensionService;
        _auditService = auditService;
        _secureDocumentStorage = secureDocumentStorage;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateForCustomer(
        int bookingId,
        DateTime requestedReturnDate,
        string? note,
        bool isForceMajeure,
        string? evidenceNote,
        string? evidenceLatitude,
        string? evidenceLongitude,
        string? evidencePlaceName,
        string? customerLiveLocation,
        List<IFormFile>? evidenceImages,
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

        var selectedEvidenceImages = CollectEvidenceImages(evidenceImages, evidenceImage);
        string? liveLocationText = null;

        if (isForceMajeure)
        {
            if (string.IsNullOrWhiteSpace(note))
            {
                TempData["ErrorMessage"] = "Vui lòng ghi rõ lý do bất khả kháng.";
                return RedirectToAction("Index", "AdminExtensions");
            }

            var imageCountError = ValidateEvidenceImageCount(selectedEvidenceImages);
            if (imageCountError is not null)
            {
                TempData["ErrorMessage"] = imageCountError;
                return RedirectToAction("Index", "AdminExtensions");
            }

            if (!TryParseLiveLocation(evidenceLatitude, evidenceLongitude, customerLiveLocation, out var latitude, out var longitude))
            {
                TempData["ErrorMessage"] = "Bất khả kháng phải lấy vị trí hiện tại trực tiếp, không nhập tay vị trí.";
                return RedirectToAction("Index", "AdminExtensions");
            }

            liveLocationText =
                $"Vị trí trực tiếp: {latitude.ToString("0.000000", CultureInfo.InvariantCulture)}, " +
                longitude.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        var (imagePaths, imageError) = await SaveEvidenceImagesAsync(
            bookingId,
            selectedEvidenceImages,
            cancellationToken);

        if (imageError is not null)
        {
            TempData["ErrorMessage"] = imageError;
            return RedirectToAction("Index", "AdminExtensions");
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
                ComposeEvidence(evidenceNote, imagePaths, liveLocationText, evidencePlaceName)),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedEvidenceImages(imagePaths);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? isForceMajeure
                ? "Đã ghi nhận yêu cầu gia hạn bất khả kháng kèm ảnh, vị trí và địa điểm hiện tại."
                : "Đã ghi nhận yêu cầu gia hạn."
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

    private static IReadOnlyList<IFormFile> CollectEvidenceImages(
        List<IFormFile>? evidenceImages,
        IFormFile? evidenceImage)
    {
        var files = new List<IFormFile>();
        if (evidenceImages is not null)
        {
            files.AddRange(evidenceImages.Where(file => file.Length > 0));
        }

        if (evidenceImage is not null && evidenceImage.Length > 0)
        {
            files.Add(evidenceImage);
        }

        return files;
    }

    private static string? ValidateEvidenceImageCount(IReadOnlyList<IFormFile> images)
    {
        if (images.Count < MinimumEvidenceImageCount)
        {
            return $"Bất khả kháng phải có tối thiểu {MinimumEvidenceImageCount} ảnh minh chứng.";
        }

        if (images.Count > MaximumEvidenceImageCount)
        {
            return $"Chỉ được gửi tối đa {MaximumEvidenceImageCount} ảnh minh chứng cho một yêu cầu.";
        }

        return null;
    }

    private async Task<(IReadOnlyList<string> Paths, string? Error)> SaveEvidenceImagesAsync(
        int bookingId,
        IReadOnlyList<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var image in images)
        {
            var (path, error) = await SaveEvidenceImageAsync(bookingId, image, cancellationToken);
            if (error is not null)
            {
                DeleteSavedEvidenceImages(paths);
                return (Array.Empty<string>(), error);
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }

        return (paths, null);
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

        var storedPath = await _secureDocumentStorage.SaveAsync(
            image,
            $"extension-evidence-booking-{bookingId}",
            cancellationToken);

        return (storedPath, null);
    }

    private void DeleteSavedEvidenceImages(IEnumerable<string?> imagePaths)
    {
        foreach (var imagePath in imagePaths)
        {
            DeleteSavedEvidenceImage(imagePath);
        }
    }

    private void DeleteSavedEvidenceImage(string? imagePath) =>
        _secureDocumentStorage.Delete(imagePath);

    private static string ComposeEvidence(
        string? evidenceNote,
        IReadOnlyList<string> imagePaths,
        string? liveLocationText,
        string? evidencePlaceName)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(evidenceNote))
        {
            parts.AddRange(
                evidenceNote
                    .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Where(line => !line.StartsWith("Vị trí trực tiếp:", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.StartsWith("Địa điểm:", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.StartsWith("Ảnh minh chứng:", StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(liveLocationText))
        {
            parts.Add(liveLocationText);
        }

        if (!string.IsNullOrWhiteSpace(evidencePlaceName))
        {
            parts.Add($"Địa điểm: {evidencePlaceName.Trim()}");
        }

        if (imagePaths.Count > 0)
        {
            parts.Add($"Ảnh minh chứng: {string.Join("; ", imagePaths)}");
        }

        return string.Join(" | ", parts);
    }

    private static bool TryParseLiveLocation(
        string? latitudeText,
        string? longitudeText,
        string? legacyLocation,
        out decimal latitude,
        out decimal longitude)
    {
        if (TryParseCoordinates(latitudeText, longitudeText, out latitude, out longitude))
        {
            return true;
        }

        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(legacyLocation))
        {
            return false;
        }

        var parts = legacyLocation
            .Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Replace(',', '.'))
            .ToArray();

        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (TryParseCoordinates(parts[index], parts[index + 1], out latitude, out longitude))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseCoordinates(
        string? latitudeText,
        string? longitudeText,
        out decimal latitude,
        out decimal longitude)
    {
        latitude = 0;
        longitude = 0;

        var normalizedLatitude = latitudeText?.Trim().Replace(',', '.');
        var normalizedLongitude = longitudeText?.Trim().Replace(',', '.');

        var latitudeOk = decimal.TryParse(
            normalizedLatitude,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out latitude);

        var longitudeOk = decimal.TryParse(
            normalizedLongitude,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out longitude);

        return latitudeOk && longitudeOk &&
               latitude is >= -90 and <= 90 &&
               longitude is >= -180 and <= 180;
    }
}
