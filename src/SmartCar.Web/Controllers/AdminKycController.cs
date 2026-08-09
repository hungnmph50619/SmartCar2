using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminKycController : Controller
{
    private static readonly string[] RequiredKycTypes =
    {
        DocumentTypes.CitizenId,
        DocumentTypes.CitizenIdBack,
        DocumentTypes.DrivingLicense,
        DocumentTypes.DrivingLicenseBack
    };

    private readonly IDocumentService _documentService;
    private readonly ApplicationDbContext _dbContext;

    public AdminKycController(
        IDocumentService documentService,
        ApplicationDbContext dbContext)
    {
        _documentService = documentService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var pendingPackages = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.Status == DocumentStatus.Pending &&
                RequiredKycTypes.Contains(document.DocumentType))
            .GroupBy(document => document.CustomerId)
            .Select(group => new
            {
                CustomerId = group.Key,
                TypeCount = group.Select(document => document.DocumentType).Distinct().Count(),
                SubmittedAt = group.Max(document => document.UpdatedAt)
            })
            .Where(item => item.TypeCount == RequiredKycTypes.Length)
            .OrderByDescending(item => item.SubmittedAt)
            .ToListAsync(cancellationToken);

        if (pendingPackages.Count == 0)
        {
            return View(new AdminKycQueueViewModel());
        }

        var customerIds = pendingPackages.Select(item => item.CustomerId).ToList();
        var customers = await _dbContext.Users
            .AsNoTracking()
            .Where(user => customerIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
                user.PhoneNumber
            })
            .ToListAsync(cancellationToken);

        var customersById = customers.ToDictionary(item => item.Id);
        var items = pendingPackages
            .Where(package => customersById.ContainsKey(package.CustomerId))
            .Select(package =>
            {
                var customer = customersById[package.CustomerId];
                return new AdminKycQueueItemViewModel
                {
                    CustomerId = customer.Id,
                    CustomerName = customer.FullName,
                    CustomerEmail = customer.Email ?? string.Empty,
                    CustomerPhone = customer.PhoneNumber,
                    SubmittedAt = package.SubmittedAt
                };
            })
            .ToList();

        return View(new AdminKycQueueViewModel { Items = items });
    }

    [HttpGet]
    public async Task<IActionResult> Review(
        string customerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return BadRequest();
        }

        var customer = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == customerId)
            .Select(user => new { user.Id, user.FullName, user.Email })
            .FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        var documents = await _documentService.GetCustomerDocumentsAsync(customerId, cancellationToken);
        if (!RequiredKycTypes.All(type => documents.Any(item => item.DocumentType == type)))
        {
            TempData["ErrorMessage"] = "Khách hàng chưa gửi đủ toàn bộ hồ sơ KYC.";
            return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
        }

        return View(new AdminKycPackageReviewViewModel
        {
            CustomerId = customer.Id,
            CustomerName = customer.FullName,
            CustomerEmail = customer.Email ?? string.Empty,
            Documents = documents
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyAll(
        string customerId,
        CancellationToken cancellationToken)
    {
        var documents = await _documentService.GetCustomerDocumentsAsync(customerId, cancellationToken);
        var package = RequiredKycTypes
            .Select(type => documents.FirstOrDefault(item => item.DocumentType == type))
            .ToList();

        if (package.Any(item => item is null))
        {
            TempData["ErrorMessage"] = "Khách hàng chưa gửi đủ CCCD và GPLX gồm cả mặt trước lẫn mặt sau.";
            return RedirectToAction(nameof(Review), new { customerId });
        }

        if (package.Any(item => item!.Status != DocumentStatus.Pending))
        {
            TempData["ErrorMessage"] = "Chỉ có thể duyệt một lần khi toàn bộ CCCD và GPLX đều đang chờ xác minh.";
            return RedirectToAction(nameof(Review), new { customerId });
        }

        if (package.Any(item => !item!.HasRequiredData))
        {
            TempData["ErrorMessage"] = "Hồ sơ KYC chưa có đủ dữ liệu bắt buộc để xác minh.";
            return RedirectToAction(nameof(Review), new { customerId });
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
            return RedirectToAction(nameof(Review), new { customerId });
        }

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

        await MarkKycWorkHandledAsync(customerId, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã duyệt toàn bộ hồ sơ KYC gồm CCCD và GPLX trong một lần.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestResubmission(
        string customerId,
        string reason,
        string? additionalNote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do yêu cầu khách hàng cập nhật hồ sơ.";
            return RedirectToAction(nameof(Review), new { customerId });
        }

        var documents = await _documentService.GetCustomerDocumentsAsync(customerId, cancellationToken);
        var package = RequiredKycTypes
            .Select(type => documents.FirstOrDefault(item => item.DocumentType == type))
            .ToList();

        if (package.Any(item => item is null))
        {
            TempData["ErrorMessage"] = "Khách hàng chưa có đủ hồ sơ KYC để xử lý.";
            return RedirectToAction(nameof(Review), new { customerId });
        }

        var fullReason = reason.Trim();
        if (!string.IsNullOrWhiteSpace(additionalNote))
        {
            fullReason = $"{fullReason}. {additionalNote.Trim()}";
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var startedAt = DateTime.UtcNow.AddSeconds(-2);
        var errors = new List<string>();

        foreach (var document in package.Cast<DocumentDto>())
        {
            if (document.Status != DocumentStatus.Pending)
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

        if (errors.Count > 0)
        {
            TempData["ErrorMessage"] = string.Join("; ", errors.Distinct());
            return RedirectToAction(nameof(Review), new { customerId });
        }

        var generatedCustomerNotifications = await _dbContext.Notifications
            .Where(item => item.UserId == customerId &&
                           item.CreatedAt >= startedAt &&
                           (item.Title == "Cần gửi lại giấy tờ" || item.Title == "Giấy tờ cần được cập nhật"))
            .ToListAsync(cancellationToken);
        if (generatedCustomerNotifications.Count > 0)
        {
            _dbContext.Notifications.RemoveRange(generatedCustomerNotifications);
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = customerId,
            Title = "Hồ sơ KYC cần cập nhật",
            Message = $"Quản trị viên yêu cầu bạn cập nhật lại CCCD và GPLX. Lý do: {fullReason}"
        });

        await MarkKycWorkHandledAsync(customerId, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã yêu cầu khách hàng cập nhật lại hồ sơ KYC và đóng yêu cầu chờ duyệt.";
        return RedirectToAction(nameof(Index));
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

        if (errors.Count == 0)
        {
            await MarkKycWorkHandledAsync(customerId, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        TempData[errors.Count == 0 ? "SuccessMessage" : "ErrorMessage"] = errors.Count == 0
            ? "Đã yêu cầu khách hàng cập nhật lại thông tin và cả hai mặt GPLX."
            : string.Join("; ", errors.Distinct());

        return RedirectToAction("Details", "AdminCustomers", new { id = customerId, tab = "documents" });
    }

    private async Task MarkKycWorkHandledAsync(string customerId, CancellationToken cancellationToken)
    {
        var packageTitle = $"Hồ sơ KYC chờ duyệt|{customerId}";
        var adminNotifications = await _dbContext.Notifications
            .Where(item => item.Title == packageTitle && !item.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var notification in adminNotifications)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
        }
    }
}
