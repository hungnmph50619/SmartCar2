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
public sealed class KycPackageController : Controller
{
    private const long MaximumDocumentImageBytes = 5 * 1024 * 1024;

    private static readonly string[] RequiredKycTypes =
    {
        DocumentTypes.CitizenId,
        DocumentTypes.CitizenIdBack,
        DocumentTypes.DrivingLicense,
        DocumentTypes.DrivingLicenseBack
    };

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _documentStorage;
    private readonly IAuditService _auditService;

    public KycPackageController(
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
    public async Task<IActionResult> Submit(
        KycPackageSubmitViewModel model,
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

        await ValidatePackageAsync(model, returnDate, cancellationToken);
        if (!model.ConfirmSamePerson)
        {
            ModelState.AddModelError(
                nameof(KycPackageSubmitViewModel.ConfirmSamePerson),
                "Bạn cần xác nhận CCCD và GPLX thuộc cùng một người trước khi gửi hồ sơ.");
        }

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = BuildModelStateErrorMessage();
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var citizen = model.CitizenIdVerification;
        var license = model.DrivingLicenseVerification;
        var citizenNumber = citizen.DocumentNumber.Trim();
        var licenseNumber = license.DocumentNumber.Trim().ToUpperInvariant();
        var holderName = NormalizePersonName(citizen.FullNameOnDocument);

        var citizenDuplicate = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                document.CustomerId != user.Id &&
                (document.DocumentType == DocumentTypes.CitizenId ||
                 document.DocumentType == DocumentTypes.CitizenIdBack) &&
                document.DocumentNumber == citizenNumber,
                cancellationToken);

        if (citizenDuplicate)
        {
            TempData["ErrorMessage"] = "Số CCCD đã được sử dụng bởi tài khoản khác.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var licenseDuplicate = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                document.CustomerId != user.Id &&
                (document.DocumentType == DocumentTypes.DrivingLicense ||
                 document.DocumentType == DocumentTypes.DrivingLicenseBack) &&
                document.DocumentNumber == licenseNumber,
                cancellationToken);

        if (licenseDuplicate)
        {
            TempData["ErrorMessage"] = "Số GPLX đã được sử dụng bởi tài khoản khác.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var existingDocuments = await _dbContext.CustomerDocuments
            .Where(document =>
                document.CustomerId == user.Id &&
                RequiredKycTypes.Contains(document.DocumentType))
            .ToListAsync(cancellationToken);

        var packageAlreadyPending = RequiredKycTypes.All(type =>
            existingDocuments.Any(document =>
                document.DocumentType == type &&
                document.Status == DocumentStatus.Pending));

        if (packageAlreadyPending)
        {
            TempData["ErrorMessage"] = "CCCD và GPLX của bạn đang chờ Quản trị viên xác minh. Không cần gửi lại.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }

        var previousPaths = existingDocuments
            .Where(document => !string.IsNullOrWhiteSpace(document.ImagePath))
            .ToDictionary(document => document.DocumentType, document => document.ImagePath);

        var newPaths = new List<string>();
        try
        {
            var citizenFrontPath = await SaveAsync(citizen.FrontImage!, user.Id, newPaths, cancellationToken);
            var citizenBackPath = await SaveAsync(citizen.BackImage!, user.Id, newPaths, cancellationToken);
            var licenseFrontPath = await SaveAsync(license.FrontImage!, user.Id, newPaths, cancellationToken);
            var licenseBackPath = await SaveAsync(license.BackImage!, user.Id, newPaths, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var citizenFront = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.CitizenId, cancellationToken);
            var citizenBack = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.CitizenIdBack, cancellationToken);
            var licenseFront = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.DrivingLicense, cancellationToken);
            var licenseBack = await GetOrCreateDocumentAsync(user.Id, DocumentTypes.DrivingLicenseBack, cancellationToken);

            ApplyDocument(citizenFront, citizenNumber, citizen.ExpiryDate!.Value, citizenFrontPath);
            ApplyDocument(citizenBack, citizenNumber, citizen.ExpiryDate.Value, citizenBackPath);
            ApplyDocument(licenseFront, licenseNumber, license.ExpiryDate!.Value, licenseFrontPath);
            ApplyDocument(licenseBack, licenseNumber, license.ExpiryDate.Value, licenseBackPath);

            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var document in new[] { citizenFront, citizenBack })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    holderName,
                    citizen.DateOfBirth!.Value.Date,
                    citizen.Gender.Trim(),
                    null,
                    null,
                    null,
                    cancellationToken);
            }

            // GPLX dùng cùng chủ hồ sơ với CCCD. Khách không phải nhập lại họ tên;
            // Quản trị viên đối chiếu trực tiếp tên trên ảnh GPLX với CCCD.
            foreach (var document in new[] { licenseFront, licenseBack })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    holderName,
                    null,
                    null,
                    null,
                    null,
                    license.LicenseClass.Trim().ToUpperInvariant(),
                    cancellationToken);
            }

            await AddSingleKycWorkNotificationAsync(user, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            DeleteReplacedImages(previousPaths, newPaths);

            await _auditService.WriteAsync(
                user.Id,
                "SubmitKycPackage",
                nameof(CustomerDocument),
                user.Id,
                "Gửi một lần toàn bộ hồ sơ xác minh danh tính gồm CCCD và GPLX, mỗi loại có mặt trước và mặt sau; người dùng xác nhận hai giấy tờ thuộc cùng một người.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            TempData["SuccessMessage"] =
                "Đã gửi toàn bộ hồ sơ CCCD và GPLX. Quản trị viên sẽ đối chiếu và xử lý hồ sơ trong một lần.";
            return RedirectToProfile(returnVehicleId, pickupDate, returnDate);
        }
        catch
        {
            foreach (var path in newPaths)
            {
                _documentStorage.Delete(path);
            }

            throw;
        }
    }

    private async Task ValidatePackageAsync(
        KycPackageSubmitViewModel model,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var citizen = model.CitizenIdVerification;
        var license = model.DrivingLicenseVerification;

        await ValidateImageAsync("CitizenIdVerification.FrontImage", citizen.FrontImage, cancellationToken);
        await ValidateImageAsync("CitizenIdVerification.BackImage", citizen.BackImage, cancellationToken);
        await ValidateImageAsync("DrivingLicenseVerification.FrontImage", license.FrontImage, cancellationToken);
        await ValidateImageAsync("DrivingLicenseVerification.BackImage", license.BackImage, cancellationToken);

        if (await ImageFileValidator.HaveSameContentAsync(citizen.FrontImage, citizen.BackImage, cancellationToken))
        {
            ModelState.AddModelError("CitizenIdVerification.BackImage", "Ảnh CCCD mặt trước và mặt sau phải là hai ảnh khác nhau.");
        }

        if (await ImageFileValidator.HaveSameContentAsync(license.FrontImage, license.BackImage, cancellationToken))
        {
            ModelState.AddModelError("DrivingLicenseVerification.BackImage", "Ảnh GPLX mặt trước và mặt sau phải là hai ảnh khác nhau.");
        }

        if (citizen.DateOfBirth.HasValue && citizen.DateOfBirth.Value.Date > DateTime.Today.AddYears(-18))
        {
            ModelState.AddModelError("CitizenIdVerification.DateOfBirth", "Khách thuê xe phải đủ 18 tuổi.");
        }

        ValidateExpiryDate(
            "CCCD",
            "CitizenIdVerification.ExpiryDate",
            citizen.ExpiryDate,
            returnDate);

        ValidateExpiryDate(
            "GPLX",
            "DrivingLicenseVerification.ExpiryDate",
            license.ExpiryDate,
            returnDate);
    }

    private async Task ValidateImageAsync(
        string key,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(
            file,
            MaximumDocumentImageBytes,
            cancellationToken);

        if (error is not null)
        {
            ModelState.AddModelError(key, error);
        }
    }

    private void ValidateExpiryDate(
        string documentName,
        string expiryDateKey,
        DateTime? expiryDate,
        DateTime? returnDate)
    {
        if (expiryDate.HasValue && expiryDate.Value.Date < DateTime.Today)
        {
            ModelState.AddModelError(expiryDateKey, $"{documentName} đã hết hạn.");
        }

        if (returnDate.HasValue && expiryDate.HasValue && expiryDate.Value.Date < returnDate.Value.Date)
        {
            ModelState.AddModelError(
                expiryDateKey,
                $"{documentName} phải còn hiệu lực ít nhất đến ngày trả xe {returnDate.Value:dd/MM/yyyy}.");
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

    private async Task<CustomerDocument> GetOrCreateDocumentAsync(
        string customerId,
        string documentType,
        CancellationToken cancellationToken)
    {
        var document = await _dbContext.CustomerDocuments
            .FirstOrDefaultAsync(item =>
                item.CustomerId == customerId && item.DocumentType == documentType,
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

    private async Task AddSingleKycWorkNotificationAsync(
        ApplicationUser customer,
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

        // Giữ khóa nội bộ cũ để tương thích với các thông báo đang có trong database.
        var title = $"Hồ sơ KYC chờ duyệt|{customer.Id}";
        var oldUnread = await _dbContext.Notifications
            .Where(item => adminIds.Contains(item.UserId) &&
                           !item.IsRead &&
                           item.Title == title)
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
                Message = $"{customer.FullName} đã gửi đủ CCCD và GPLX trong một lần. Hãy mở hồ sơ để đối chiếu và ra quyết định xác minh."
            });
        }
    }

    private void DeleteReplacedImages(
        IReadOnlyDictionary<string, string> previousPaths,
        IReadOnlyCollection<string> newPaths)
    {
        foreach (var previousPath in previousPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!newPaths.Contains(previousPath, StringComparer.OrdinalIgnoreCase))
            {
                _documentStorage.Delete(previousPath);
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
            : "Vui lòng kiểm tra lại toàn bộ thông tin CCCD, GPLX và 4 ảnh giấy tờ.";
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
