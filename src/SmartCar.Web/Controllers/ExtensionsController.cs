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
        IFormFile? evidenceImage,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
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

        var composedEvidence = ComposeEvidence(evidenceNote, imagePath);
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
                ? "Đã gửi yêu cầu gia hạn bất khả kháng. SmartCar sẽ kiểm tra minh chứng, vị trí và lịch xe trước khi quyết định."
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

        var (imagePath, imageError) = await SaveEvidenceImageAsync(
            bookingId,
            evidenceImage,
            cancellationToken);

        if (imageError is not null)
        {
            TempData["ErrorMessage"] = imageError;
            return RedirectToAction(nameof(Index));
        }

        var composedEvidence = ComposeEvidence(evidenceNote, imagePath);
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
            ? "Đã bổ sung minh chứng và gửi lại yêu cầu gia hạn."
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
