using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class DocumentsController : Controller
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly IDocumentService _documentService;
    private readonly IWebHostEnvironment _environment;

    public DocumentsController(
        IDocumentService documentService,
        IWebHostEnvironment environment)
    {
        _documentService = documentService;
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

        ViewBag.Documents = await _documentService.GetCustomerDocumentsAsync(
            customerId,
            cancellationToken);
        return View(new DocumentUploadViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(
        DocumentUploadViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        ValidateImage(model.Image);
        if (!ModelState.IsValid || model.Image is null)
        {
            ViewBag.Documents = await _documentService.GetCustomerDocumentsAsync(
                customerId,
                cancellationToken);
            return View("Index", model);
        }

        var extension = Path.GetExtension(model.Image.FileName).ToLowerInvariant();
        var relativeFolder = $"uploads/documents/{customerId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);
        var fileName = $"{model.DocumentType.ToLowerInvariant()}-{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(folder, fileName);

        await using (var stream = System.IO.File.Create(fullPath))
        {
            await model.Image.CopyToAsync(stream, cancellationToken);
        }

        var result = await _documentService.SubmitAsync(
            customerId,
            new SubmitDocumentRequest(
                model.DocumentType,
                model.DocumentNumber,
                model.ExpiryDate,
                $"/{relativeFolder}/{fileName}"),
            cancellationToken);

        if (!result.Succeeded)
        {
            System.IO.File.Delete(fullPath);
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
        }
        else
        {
            TempData["SuccessMessage"] = "Đã gửi giấy tờ. Vui lòng chờ Admin xác minh.";
        }

        return RedirectToAction(nameof(Index));
    }

    private void ValidateImage(IFormFile? image)
    {
        if (image is null || image.Length == 0)
        {
            ModelState.AddModelError(nameof(DocumentUploadViewModel.Image), "Vui lòng chọn ảnh giấy tờ.");
            return;
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(image.FileName)))
        {
            ModelState.AddModelError(nameof(DocumentUploadViewModel.Image),
                "Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.");
        }

        if (image.Length > 5 * 1024 * 1024)
        {
            ModelState.AddModelError(nameof(DocumentUploadViewModel.Image),
                "Ảnh giấy tờ không được vượt quá 5 MB.");
        }
    }
}
