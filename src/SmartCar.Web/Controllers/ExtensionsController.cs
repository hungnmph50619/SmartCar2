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
        IFormFile? evidenceImage,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        string? liveLocationText = null;
        if (isForceMajeure)
        {
            if (string.IsNullOrWhiteSpace(model.CustomerNote))
            {
                TempData["ErrorMessage"] = "Vui lòng nêu rõ lý do bất khả kháng.";
                return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
            }

            if (evidenceImage is null || evidenceImage.Length == 0)
            {
                TempData["ErrorMessage"] = "Trường hợp bất khả kháng bắt buộc phải có ảnh minh chứng tình trạng xe/sự cố.";
                return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
            }

            if (!TryParseLiveLocation(evidenceLatitude, evidenceLongitude, out var latitude, out var longitude))
            {
                TempData["ErrorMessage"] = "Trường hợp bất khả kháng bắt buộc phải lấy vị trí trực tiếp trước khi gửi yêu cầu.";
                return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
            }

            liveLocationText =
                $"Vị trí trực tiếp: {latitude.ToString("0.000000", CultureInfo.InvariantCulture)}, " +
                longitude.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        var (imagePath, imageError) = await SaveEvidenceImageAsync(
            model.BookingId,
            evidenceImage,
            cancellationToken);

        if (imageError is not null)
        {
            TempData["ErrorMessage"] = imageError;
            return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
        }

        var composedEvidence = ComposeEvidence(evidenceNote, imagePath, liveLocationText);
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
            DeleteSavedEvidenceImage(imagePath);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? isForceMajeure
                ? "Đã gửi yêu cầu gia hạn bất khả kháng kèm ảnh và vị trí trực tiếp. SmartCar sẽ kiểm tra lịch xe và phương án cho khách kế tiếp trước khi quyết định."
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
        string? customerNote,
        IFormFile? evidenceImage,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        if (evidenceImage is null || evidenceImage.Length == 0)
        {
            TempData["ErrorMessage"] = "Vui lòng gửi lại ảnh minh chứng theo yêu cầu của SmartCar.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(evidenceNote) ||
            !evidenceNote.Contains("Vị trí trực tiếp:", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Vui lòng bấm Lấy vị trí hiện tại và gửi lại vị trí trực tiếp cùng minh chứng.";
            return RedirectToAction(nameof(Index));
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

        var composedEvidence = ComposeEvidence(evidenceNote, imagePath, null);
        var result = await _extensionService.SupplementEvidenceAsync(
            extensionId,
            customerId,
            composedEvidence,
            customerNote,
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedEvidenceImage(imagePath);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã bổ sung ảnh, vị trí trực tiếp và gửi lại yêu cầu gia hạn."
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

    private static string ComposeEvidence(
        string? evidenceNote,
        string? imagePath,
        string? liveLocationText)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(evidenceNote))
        {
            parts.AddRange(
                evidenceNote
                    .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0));
        }

        if (!string.IsNullOrWhiteSpace(liveLocationText) &&
            !parts.Any(part => part.Contains("Vị trí trực tiếp:", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(liveLocationText);
        }

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            parts.Add($"Ảnh minh chứng: {imagePath}");
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
