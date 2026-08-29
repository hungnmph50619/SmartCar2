using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class ExtensionsController : Controller
{
    private const long MaximumEvidenceImageBytes = 5 * 1024 * 1024;
    private const int MinimumEvidenceImageCount = 2;
    private const int MaximumEvidenceImageCount = 8;

    private readonly IExtensionService _extensionService;
    private readonly IWebHostEnvironment _environment;

    public ExtensionsController(
        IExtensionService extensionService,
        IWebHostEnvironment environment)
    {
        _extensionService = extensionService;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        return View(await _extensionService.GetCustomerExtensionsAsync(
            customerId,
            cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitRequest(
        ExtensionRequestViewModel model,
        bool isForceMajeure,
        string? evidenceNote,
        string? evidenceLatitude,
        string? evidenceLongitude,
        string? evidencePlaceName,
        List<IFormFile>? evidenceImages,
        IFormFile? evidenceImage,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var selectedEvidenceImages = CollectEvidenceImages(evidenceImages, evidenceImage);
        string? liveLocationText = null;
        if (isForceMajeure)
        {
            if (string.IsNullOrWhiteSpace(model.CustomerNote))
            {
                TempData["ErrorMessage"] = "Vui lòng nêu rõ lý do bất khả kháng.";
                return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
            }

            var imageCountError = ValidateEvidenceImageCount(selectedEvidenceImages);
            if (imageCountError is not null)
            {
                TempData["ErrorMessage"] = imageCountError;
                return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
            }

            if (!TryParseLiveLocation(evidenceLatitude, evidenceLongitude, out var latitude, out var longitude))
            {
                TempData["ErrorMessage"] = "Trường hợp bất khả kháng bắt buộc phải lấy vị trí hiện tại trực tiếp trước khi gửi yêu cầu.";
                return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
            }

            liveLocationText =
                $"Vị trí trực tiếp: {latitude.ToString("0.000000", CultureInfo.InvariantCulture)}, " +
                longitude.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        var (imagePaths, imageError) = await SaveEvidenceImagesAsync(
            model.BookingId,
            selectedEvidenceImages,
            cancellationToken);

        if (imageError is not null)
        {
            TempData["ErrorMessage"] = imageError;
            return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
        }

        var composedEvidence = ComposeEvidence(evidenceNote, imagePaths, liveLocationText, evidencePlaceName);
        var result = ModelState.IsValid
            ? await _extensionService.RequestAsync(
                customerId,
                new RequestExtensionRequest(
                    model.BookingId,
                    model.RequestedReturnDate,
                    model.CustomerNote,
                    isForceMajeure,
                    composedEvidence),
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Thông tin gia hạn không hợp lệ.");

        if (!result.Succeeded)
        {
            DeleteSavedEvidenceImages(imagePaths);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? isForceMajeure
                ? "Đã gửi yêu cầu gia hạn bất khả kháng kèm ảnh, vị trí và địa điểm hiện tại. SmartCar sẽ kiểm tra lịch xe và phương án cho khách kế tiếp trước khi quyết định."
                : "Đã gửi yêu cầu gia hạn."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SupplementEvidence(
        int extensionId,
        int bookingId,
        string? evidenceNote,
        string? evidenceLatitude,
        string? evidenceLongitude,
        string? evidencePlaceName,
        string? customerNote,
        List<IFormFile>? evidenceImages,
        IFormFile? evidenceImage,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var selectedEvidenceImages = CollectEvidenceImages(evidenceImages, evidenceImage);
        var imageCountError = ValidateEvidenceImageCount(selectedEvidenceImages);
        if (imageCountError is not null)
        {
            TempData["ErrorMessage"] = imageCountError;
            return RedirectToAction(nameof(Index));
        }

        if (!TryParseLiveLocation(evidenceLatitude, evidenceLongitude, out var latitude, out var longitude))
        {
            TempData["ErrorMessage"] = "Vui lòng bấm Lấy vị trí hiện tại và gửi lại vị trí trực tiếp cùng minh chứng.";
            return RedirectToAction(nameof(Index));
        }

        var liveLocationText =
            $"Vị trí trực tiếp: {latitude.ToString("0.000000", CultureInfo.InvariantCulture)}, " +
            longitude.ToString("0.000000", CultureInfo.InvariantCulture);

        var (imagePaths, imageError) = await SaveEvidenceImagesAsync(
            bookingId,
            selectedEvidenceImages,
            cancellationToken);

        if (imageError is not null)
        {
            TempData["ErrorMessage"] = imageError;
            return RedirectToAction(nameof(Index));
        }

        var composedEvidence = ComposeEvidence(evidenceNote, imagePaths, liveLocationText, evidencePlaceName);
        var result = await _extensionService.SupplementEvidenceAsync(
            extensionId,
            customerId,
            composedEvidence,
            customerNote,
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedEvidenceImages(imagePaths);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã bổ sung ảnh, vị trí trực tiếp và gửi lại yêu cầu gia hạn."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index));
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
            return $"Trường hợp bất khả kháng phải có tối thiểu {MinimumEvidenceImageCount} ảnh minh chứng.";
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

    private void DeleteSavedEvidenceImages(IEnumerable<string?> imagePaths)
    {
        foreach (var imagePath in imagePaths)
        {
            DeleteSavedEvidenceImage(imagePath);
        }
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
