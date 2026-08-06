using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminDocumentsController : Controller
{
    private readonly IDocumentService _documentService;
    private readonly ISecureDocumentStorage _storage;

    public AdminDocumentsController(
        IDocumentService documentService,
        ISecureDocumentStorage storage)
    {
        _documentService = documentService;
        _storage = storage;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return RedirectToAction("Index", "AdminCustomers", new { profileStatus = "Pending" });
    }

    [HttpGet]
    public async Task<IActionResult> ViewImage(
        int id,
        CancellationToken cancellationToken)
    {
        var document = await _documentService.GetDocumentAsync(id, cancellationToken);
        if (document is null ||
            !_storage.TryResolve(document.ImagePath, out var fullPath, out var contentType))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return PhysicalFile(fullPath, contentType, enableRangeProcessing: false);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(int id, CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var document = await _documentService.GetDocumentAsync(id, cancellationToken);
        var result = await _documentService.VerifyAsync(id, adminId, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xác minh giấy tờ."
            : string.Join("; ", result.Errors);
        return document is null
            ? RedirectToAction("Index", "AdminCustomers")
            : RedirectToAction("Details", "AdminCustomers", new { id = document.CustomerId, tab = "documents" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        RejectDocumentViewModel model,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var document = await _documentService.GetDocumentAsync(model.DocumentId, cancellationToken);
        var result = ModelState.IsValid
            ? await _documentService.RejectAsync(
                model.DocumentId,
                adminId,
                model.Reason,
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Vui lòng nhập lý do yêu cầu gửi lại.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã yêu cầu khách hàng gửi lại giấy tờ."
            : string.Join("; ", result.Errors);
        return document is null
            ? RedirectToAction("Index", "AdminCustomers")
            : RedirectToAction("Details", "AdminCustomers", new { id = document.CustomerId, tab = "documents" });
    }
}
