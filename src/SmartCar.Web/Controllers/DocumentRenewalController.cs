using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class DocumentRenewalController : Controller
{
    private const long MaximumDocumentImageBytes = 5 * 1024 * 1024;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _documentStorage;
    private readonly IAuditService _auditService;

    public DocumentRenewalController(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext dbContext,
        ISecureDocumentStorage documentStorage,
        IAuditService auditService)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _documentStorage = documentStorage;
        _auditService = auditService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCitizenId(
        [Bind(Prefix = "CitizenIdVerification")] KycCitizenIdInputViewModel model,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await ValidateCitizenIdAsync(model, returnDate, cancellationToken);
        if (!ModelState.IsValid || model.FrontImage is null || model.BackImage is null)
        {
            TempData["ErrorMessage"] = BuildModelStateErrorMessage();
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var documentNumber = model.DocumentNumber.Trim();
        var duplicate = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                document.CustomerId != user.Id &&
                (document.DocumentType == DocumentTypes.CitizenId ||
                 document.DocumentType == DocumentTypes.CitizenIdBack) &&
                document.DocumentNumber == documentNumber,
                cancellationToken);

        if (duplicate)
        {
            TempData["ErrorMessage"] = "Số CCCD đã được sử dụng bởi tài khoản khác.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var existing = await _dbContext.CustomerDocuments
            .Where(document =>
                document.CustomerId == user.Id &&
                (document.DocumentType == DocumentTypes.CitizenId ||
                 document.DocumentType == DocumentTypes.CitizenIdBack))
            .ToListAsync(cancellationToken);

        if (existing.Count == 2 && existing.All(document => document.Status == DocumentStatus.Pending))
        {
            TempData["ErrorMessage"] = "CCCD mới của bạn đang chờ Quản trị viên xác minh. Không cần gửi lại.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var oldPaths = existing
            .Where(document => !string.IsNullOrWhiteSpace(document.ImagePath))
            .Select(document => document.ImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newPaths = new List<string>();
        try
        {
            var frontPath = await SaveAsync(model.FrontImage, user.Id, newPaths, cancellationToken);
            var backPath = await SaveAsync(model.BackImage, user.Id, newPaths, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var front = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.CitizenId, cancellationToken);
            var back = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.CitizenIdBack, cancellationToken);

            ApplyDocument(front, documentNumber, model.ExpiryDate!.Value, frontPath);
            ApplyDocument(back, documentNumber, model.ExpiryDate.Value, backPath);
            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var document in new[] { front, back })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    NormalizePersonName(model.FullNameOnDocument),
                    model.DateOfBirth!.Value.Date,
                    model.Gender.Trim(),
                    null,
                    null,
                    null,
                    cancellationToken);
            }

            await AddAdminNotificationAsync(
                user,
                $"CCCD cập nhật chờ duyệt|{user.Id}",
                $"{user.FullName} vừa gửi CCCD mới để cập nhật. Hãy đối chiếu hai mặt và xác minh lại giấy tờ.",
                cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            DeleteReplacedImages(oldPaths, newPaths);

            await _auditService.WriteAsync(
                user.Id,
                "RenewCitizenId",
                nameof(CustomerDocument),
                user.Id,
                "Gửi CCCD mới gồm thông tin cần thiết và hai ảnh để cập nhật; không yêu cầu nhập ngày cấp.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            TempData["SuccessMessage"] =
                "Đã gửi CCCD mới. Giấy tờ đang chờ Quản trị viên xác minh lại.";
        }
        catch
        {
            foreach (var path in newPaths)
            {
                _documentStorage.Delete(path);
            }

            throw;
        }

        return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitDrivingLicense(
        [Bind(Prefix = "DrivingLicenseVerification")] KycDrivingLicenseInputViewModel model,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await ValidateDrivingLicenseAsync(model, returnDate, cancellationToken);
        if (!ModelState.IsValid || model.FrontImage is null || model.BackImage is null)
        {
            TempData["ErrorMessage"] = BuildModelStateErrorMessage();
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var documentNumber = model.DocumentNumber.Trim().ToUpperInvariant();
        var duplicate = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                document.CustomerId != user.Id &&
                (document.DocumentType == DocumentTypes.DrivingLicense ||
                 document.DocumentType == DocumentTypes.DrivingLicenseBack) &&
                document.DocumentNumber == documentNumber,
                cancellationToken);

        if (duplicate)
        {
            TempData["ErrorMessage"] = "Số GPLX đã được sử dụng bởi tài khoản khác.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var existing = await _dbContext.CustomerDocuments
            .Where(document =>
                document.CustomerId == user.Id &&
                (document.DocumentType == DocumentTypes.DrivingLicense ||
                 document.DocumentType == DocumentTypes.DrivingLicenseBack))
            .ToListAsync(cancellationToken);

        if (existing.Count == 2 && existing.All(document => document.Status == DocumentStatus.Pending))
        {
            TempData["ErrorMessage"] = "GPLX mới của bạn đang chờ Quản trị viên xác minh. Không cần gửi lại.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var oldPaths = existing
            .Where(document => !string.IsNullOrWhiteSpace(document.ImagePath))
            .Select(document => document.ImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newPaths = new List<string>();
        try
        {
            var frontPath = await SaveAsync(model.FrontImage, user.Id, newPaths, cancellationToken);
            var backPath = await SaveAsync(model.BackImage, user.Id, newPaths, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var front = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.DrivingLicense, cancellationToken);
            var back = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.DrivingLicenseBack, cancellationToken);

            ApplyDocument(front, documentNumber, model.ExpiryDate!.Value, frontPath);
            ApplyDocument(back, documentNumber, model.ExpiryDate.Value, backPath);
            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var document in new[] { front, back })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    NormalizePersonName(user.FullName),
                    null,
                    null,
                    null,
                    null,
                    model.LicenseClass.Trim().ToUpperInvariant(),
                    cancellationToken);
            }

            await AddAdminNotificationAsync(
                user,
                $"GPLX cập nhật chờ duyệt|{user.Id}",
                $"{user.FullName} vừa gửi GPLX mới để cập nhật. Hãy đối chiếu hai mặt và xác minh lại giấy tờ.",
                cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            DeleteReplacedImages(oldPaths, newPaths);

            await _auditService.WriteAsync(
                user.Id,
                "RenewDrivingLicense",
                nameof(CustomerDocument),
                user.Id,
                "Gửi GPLX mới gồm số giấy phép, hạng, ngày hết hạn và hai ảnh; không yêu cầu nhập ngày cấp.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            TempData["SuccessMessage"] =
                "Đã gửi GPLX mới. Giấy phép đang chờ Quản trị viên xác minh lại.";
        }
        catch
        {
            foreach (var path in newPaths)
            {
                _documentStorage.Delete(path);
            }

            throw;
        }

        return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
    }

    private async Task ValidateCitizenIdAsync(
        KycCitizenIdInputViewModel model,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        await ValidateImageAsync("CitizenIdVerification.FrontImage", model.FrontImage, cancellationToken);
        await ValidateImageAsync("CitizenIdVerification.BackImage", model.BackImage, cancellationToken);

        if (await ImageFileValidator.HaveSameContentAsync(model.FrontImage, model.BackImage, cancellationToken))
        {
            ModelState.AddModelError("CitizenIdVerification.BackImage", "Ảnh CCCD mặt trước và mặt sau phải là hai ảnh khác nhau.");
        }

        if (model.DateOfBirth.HasValue && model.DateOfBirth.Value.Date > DateTime.Today.AddYears(-18))
        {
            ModelState.AddModelError("CitizenIdVerification.DateOfBirth", "Khách thuê xe phải đủ 18 tuổi.");
        }

        ValidateExpiryDate("CCCD", "CitizenIdVerification.ExpiryDate", model.ExpiryDate, returnDate);
    }

    private async Task ValidateDrivingLicenseAsync(
        KycDrivingLicenseInputViewModel model,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        await ValidateImageAsync("DrivingLicenseVerification.FrontImage", model.FrontImage, cancellationToken);
        await ValidateImageAsync("DrivingLicenseVerification.BackImage", model.BackImage, cancellationToken);

        if (await ImageFileValidator.HaveSameContentAsync(model.FrontImage, model.BackImage, cancellationToken))
        {
            ModelState.AddModelError("DrivingLicenseVerification.BackImage", "Ảnh GPLX mặt trước và mặt sau phải là hai ảnh khác nhau.");
        }

        ValidateExpiryDate("GPLX", "DrivingLicenseVerification.ExpiryDate", model.ExpiryDate, returnDate);
    }

    private async Task ValidateImageAsync(
        string key,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(file, MaximumDocumentImageBytes, cancellationToken);
        if (error is not null)
        {
            ModelState.AddModelError(key, error);
        }
    }

    private void ValidateExpiryDate(
        string documentName,
        string key,
        DateTime? expiryDate,
        DateTime? returnDate)
    {
        if (expiryDate.HasValue && expiryDate.Value.Date < DateTime.Today)
        {
            ModelState.AddModelError(key, $"{documentName} đã hết hạn.");
        }

        if (returnDate.HasValue && expiryDate.HasValue && expiryDate.Value.Date < returnDate.Value.Date)
        {
            ModelState.AddModelError(
                key,
                $"{documentName} mới phải còn hiệu lực ít nhất đến ngày trả xe {returnDate.Value:dd/MM/yyyy}.");
        }
    }

    private async Task<CustomerDocument> GetOrCreateDocumentAsync(
        string customerId,
        string documentType,
        CancellationToken cancellationToken)
    {
        var document = await _dbContext.CustomerDocuments
            .FirstOrDefaultAsync(item =>
                item.CustomerId == customerId &&
                item.DocumentType == documentType,
                cancellationToken);

        if (document is not null)
        {
            return document;
        }

        document = new CustomerDocument
        {
            CustomerId = customerId,
            DocumentType = documentType,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.CustomerDocuments.Add(document);
        return document;
    }

    private static void ApplyDocument(
        CustomerDocument document,
        string documentNumber,
        DateTime expiryDate,
        string imagePath)
    {
        document.DocumentNumber = documentNumber;
        document.ExpiryDate = expiryDate.Date;
        document.ImagePath = imagePath;
        document.Status = DocumentStatus.Pending;
        document.RejectionReason = null;
        document.VerifiedBy = null;
        document.VerifiedAt = null;
        document.UpdatedAt = DateTime.UtcNow;
    }

    private async Task UpdateKycMetadataAsync(
        int documentId,
        string? fullNameOnDocument,
        DateTime? dateOfBirth,
        string? gender,
        DateTime? issuedDate,
        string? permanentAddress,
        string? licenseClass,
        CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE [CustomerDocuments]
            SET [FullNameOnDocument] = {fullNameOnDocument},
                [DateOfBirth] = {dateOfBirth},
                [Gender] = {gender},
                [IssuedDate] = {issuedDate},
                [PermanentAddress] = {permanentAddress},
                [LicenseClass] = {licenseClass}
            WHERE [CustomerDocumentId] = {documentId}", cancellationToken);
    }

    private async Task AddAdminNotificationAsync(
        ApplicationUser customer,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var adminRoleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .Where(item => item.RoleId == adminRoleId)
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var oldUnread = await _dbContext.Notifications
            .Where(item => adminIds.Contains(item.UserId) && !item.IsRead && item.Title == title)
            .ToListAsync(cancellationToken);

        if (oldUnread.Count > 0)
        {
            _dbContext.Notifications.RemoveRange(oldUnread);
        }

        foreach (var adminId in adminIds)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            });
        }
    }

    private async Task<string> SaveAsync(
        IFormFile file,
        string userId,
        ICollection<string> newPaths,
        CancellationToken cancellationToken)
    {
        var path = await _documentStorage.SaveAsync(file, userId, cancellationToken);
        newPaths.Add(path);
        return path;
    }

    private void DeleteReplacedImages(
        IReadOnlyCollection<string> oldPaths,
        IReadOnlyCollection<string> newPaths)
    {
        foreach (var oldPath in oldPaths)
        {
            if (!newPaths.Contains(oldPath, StringComparer.OrdinalIgnoreCase))
            {
                _documentStorage.Delete(oldPath);
            }
        }
    }

    private string BuildModelStateErrorMessage()
    {
        var errors = ModelState.Values
            .SelectMany(value => value.Errors)
            .Select(error => error.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct()
            .ToList();

        return errors.Count > 0
            ? string.Join("; ", errors)
            : "Vui lòng kiểm tra lại thông tin và ảnh giấy tờ.";
    }

    private static string NormalizePersonName(string value) =>
        string.Join(' ', value
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private IActionResult RedirectToProfile(
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate) =>
        RedirectToAction("Index", "Profile", new
        {
            tab = "documents",
            returnVehicleId,
            pickupDate,
            returnDate
        });
}
