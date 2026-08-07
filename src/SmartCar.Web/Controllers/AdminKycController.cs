using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminKycController : Controller
{
    private readonly IDocumentService _documentService;

    public AdminKycController(IDocumentService documentService)
    {
        _documentService = documentService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyDrivingLicense(
        string customerId,
        int frontDocumentId,
        int backDocumentId,
        CancellationToken cancellationToken)
    {
        var front = await _documentService.GetDocumentAsync(frontDocumentId, cancellationToken);
        var back = await _documentService.GetDocumentAsync(backDocumentId, cancellationToken);

        if (front is null || back is null ||
            front.CustomerId != customerId || back.CustomerId != customerId ||
            front.DocumentType != DocumentTypes.DrivingLicense ||
            back.DocumentType != DocumentTypes.DrivingLicenseBack)
        {
            return NotFound();
        }

        if (!front.HasRequiredData || !back.HasRequiredData)
        {
            TempData["ErrorMessage"] = "Hồ sơ GPLX chưa có đủ thông tin và hai mặt ảnh để xác minh.";
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        if (front.Status != DocumentStatus.Pending || back.Status != DocumentStatus.Pending)
        {
            TempData["ErrorMessage"] = "Cả hai mặt GPLX phải ở trạng thái chờ xác minh.";
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        var adminId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) ?? string.Empty;
        var frontResult = await _documentService.VerifyAsync(frontDocumentId, adminId, cancellationToken);
        if (!frontResult.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", frontResult.Errors);
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        var backResult = await _documentService.VerifyAsync(backDocumentId, adminId, cancellationToken);
        TempData[backResult.Succeeded ? "SuccessMessage" : "ErrorMessage"] = backResult.Succeeded
            ? "Đã xác minh GPLX gồm thông tin, mặt trước và mặt sau."
            : string.Join("; ", backResult.Errors);

        return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDrivingLicenseResubmission(
        string customerId,
        int frontDocumentId,
        int backDocumentId,
        string reason,
        string? additionalNote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["ErrorMessage"] = "Vui lòng chọn lý do yêu cầu cập nhật GPLX.";
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        var front = await _documentService.GetDocumentAsync(frontDocumentId, cancellationToken);
        var back = await _documentService.GetDocumentAsync(backDocumentId, cancellationToken);
        if (front is null || back is null ||
            front.CustomerId != customerId || back.CustomerId != customerId ||
            front.DocumentType != DocumentTypes.DrivingLicense ||
            back.DocumentType != DocumentTypes.DrivingLicenseBack)
        {
            return NotFound();
        }

        var fullReason = reason.Trim();
        if (!string.IsNullOrWhiteSpace(additionalNote))
        {
            fullReason = $"{fullReason}. {additionalNote.Trim()}";
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var errors = new List<string>();

        foreach (var document in new[] { front, back })
        {
            if (document.Status is not (DocumentStatus.Pending or DocumentStatus.Verified))
            {
                continue;
            }

            var result = await _documentService.RejectAsync(
                document.CustomerDocumentId,
                adminId,
                fullReason,
                cancellationToken);
            if (!result.Succeeded)
            {
                errors.AddRange(result.Errors);
            }
        }

        TempData[errors.Count == 0 ? "SuccessMessage" : "ErrorMessage"] = errors.Count == 0
            ? "Đã yêu cầu khách hàng cập nhật lại thông tin và cả hai mặt GPLX."
            : string.Join("; ", errors.Distinct());

        return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
    }
}
