using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminKycController : Controller
{
    private readonly IDocumentService _documentService;
    private readonly ApplicationDbContext _dbContext;

    public AdminKycController(
        IDocumentService documentService,
        ApplicationDbContext dbContext)
    {
        _documentService = documentService;
        _dbContext = dbContext;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyAll(
        string customerId,
        CancellationToken cancellationToken)
    {
        var documents = await _documentService.GetCustomerDocumentsAsync(customerId, cancellationToken);
        var requiredTypes = new[]
        {
            DocumentTypes.CitizenId,
            DocumentTypes.CitizenIdBack,
            DocumentTypes.DrivingLicense,
            DocumentTypes.DrivingLicenseBack
        };

        var package = requiredTypes
            .Select(type => documents.FirstOrDefault(item => item.DocumentType == type))
            .ToList();

        if (package.Any(item => item is null))
        {
            TempData["ErrorMessage"] = "Khách hàng chưa gửi đủ CCCD và GPLX gồm cả mặt trước lẫn mặt sau.";
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        if (package.Any(item => item!.Status != DocumentStatus.Pending))
        {
            TempData["ErrorMessage"] = "Chỉ có thể duyệt một lần khi toàn bộ CCCD và GPLX đều đang chờ xác minh.";
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        if (package.Any(item => !item!.HasRequiredData))
        {
            TempData["ErrorMessage"] = "Hồ sơ KYC chưa có đủ dữ liệu bắt buộc để xác minh.";
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var startedAt = DateTime.UtcNow.AddSeconds(-2);
        var errors = new List<string>();

        foreach (var document in package.Cast<DocumentDto>())
        {
            var result = await _documentService.VerifyAsync(
                document.CustomerDocumentId,
                adminId,
                cancellationToken);
            if (!result.Succeeded)
            {
                errors.AddRange(result.Errors);
                break;
            }
        }

        if (errors.Count > 0)
        {
            TempData["ErrorMessage"] = string.Join("; ", errors.Distinct());
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        // VerifyAsync creates one customer notification per stored side. Replace those with one
        // clear notification for the whole KYC decision.
        var generatedCustomerNotifications = await _dbContext.Notifications
            .Where(item => item.UserId == customerId &&
                           item.CreatedAt >= startedAt &&
                           item.Title == "Giấy tờ đã được xác minh")
            .ToListAsync(cancellationToken);
        if (generatedCustomerNotifications.Count > 0)
        {
            _dbContext.Notifications.RemoveRange(generatedCustomerNotifications);
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = customerId,
            Title = "Hồ sơ KYC đã được xác minh",
            Message = "CCCD và giấy phép lái xe của bạn đã được Quản trị viên đối chiếu và xác minh. Hồ sơ KYC đã hoàn tất."
        });

        // The package is done, so clear the actionable pending notification for every admin.
        var packageTitle = $"Hồ sơ KYC chờ duyệt|{customerId}";
        var adminNotifications = await _dbContext.Notifications
            .Where(item => item.Title == packageTitle && !item.IsRead)
            .ToListAsync(cancellationToken);
        foreach (var notification in adminNotifications)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã duyệt toàn bộ hồ sơ KYC gồm CCCD và GPLX trong một lần.";
        return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
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

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
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
