using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminDocumentsController : Controller
{
    private readonly IDocumentService _documentService;

    public AdminDocumentsController(IDocumentService documentService)
    {
        _documentService = documentService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _documentService.GetPendingDocumentsAsync(cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(int id, CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _documentService.VerifyAsync(id, adminId, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xác minh giấy tờ."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        RejectDocumentViewModel model,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = ModelState.IsValid
            ? await _documentService.RejectAsync(
                model.DocumentId,
                adminId,
                model.Reason,
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Vui lòng nhập lý do từ chối.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã từ chối giấy tờ."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }
}
