using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class DocumentsController : Controller
{
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private readonly IDocumentService _documentService;
    private readonly ISecureDocumentStorage _storage;

    public DocumentsController(
        IDocumentService documentService,
        ISecureDocumentStorage storage)
    {
        _documentService = documentService;
        _storage = storage;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return RedirectToAction(
            "Index",
            "Profile",
            new { tab = "documents" });
    }

    [HttpGet]
    public async Task<IActionResult> ViewImage(
        int id,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var document = await _documentService.GetDocumentAsync(id, cancellationToken);
        if (document is null || document.CustomerId != customerId)
        {
            return NotFound();
        }

        if (!_storage.TryResolve(document.ImagePath, out var fullPath, out var contentType))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return PhysicalFile(fullPath, contentType, enableRangeProcessing: false);
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

        var imageError = await ImageFileValidator.ValidateAsync(
            model.Image,
            MaximumImageBytes,
            cancellationToken);
        if (imageError is not null)
        {
            ModelState.AddModelError(nameof(DocumentUploadViewModel.Image), imageError);
        }

        if (!ModelState.IsValid || model.Image is null)
        {
            TempData["ErrorMessage"] = "Thông tin giấy tờ chưa hợp lệ. Vui lòng gửi lại trong Hồ sơ cá nhân.";
            return RedirectToAction(
                "Index",
                "Profile",
                new { tab = "documents" });
        }

        var existingDocuments = await _documentService.GetCustomerDocumentsAsync(
            customerId,
            cancellationToken);
        var existingDocument = existingDocuments.FirstOrDefault(item =>
            item.DocumentType == model.DocumentType);

        var newStoredPath = await _storage.SaveAsync(
            model.Image,
            customerId,
            cancellationToken);

        var result = await _documentService.SubmitAsync(
            customerId,
            new SubmitDocumentRequest(
                model.DocumentType,
                model.DocumentNumber,
                model.ExpiryDate,
                newStoredPath),
            cancellationToken);

        if (!result.Succeeded)
        {
            _storage.Delete(newStoredPath);
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
        }
        else
        {
            if (existingDocument is not null &&
                !string.Equals(
                    existingDocument.ImagePath,
                    newStoredPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _storage.Delete(existingDocument.ImagePath);
            }

            TempData["SuccessMessage"] =
                "Đã gửi giấy tờ thành công. Hồ sơ đang chờ Quản trị viên xác minh.";
        }

        return RedirectToAction(
            "Index",
            "Profile",
            new { tab = "documents" });
    }
}
