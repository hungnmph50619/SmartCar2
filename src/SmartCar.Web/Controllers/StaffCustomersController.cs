using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Accounts;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff + "," + RoleNames.Admin)]
public sealed class StaffCustomersController : Controller
{
    private const long MaximumKycImageBytes = 5 * 1024 * 1024;

    private readonly IAccountService _accountService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IDocumentService _documentService;
    private readonly IAuditService _auditService;
    private readonly ISecureDocumentStorage _secureDocumentStorage;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<StaffCustomersController> _logger;

    public StaffCustomersController(
        IAccountService accountService,
        ApplicationDbContext dbContext,
        IDocumentService documentService,
        IAuditService auditService,
        ISecureDocumentStorage secureDocumentStorage,
        UserManager<ApplicationUser> userManager,
        ILogger<StaffCustomersController> logger)
    {
        _accountService = accountService;
        _dbContext = dbContext;
        _documentService = documentService;
        _auditService = auditService;
        _secureDocumentStorage = secureDocumentStorage;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? query,
        CancellationToken cancellationToken)
    {
        var customerRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Customer)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(customerRoleId))
        {
            return View(new StaffCustomerIndexViewModel { Query = query });
        }

        var customerQuery = _dbContext.Users
            .AsNoTracking()
            .Where(user => _dbContext.UserRoles.Any(userRole =>
                userRole.UserId == user.Id &&
                userRole.RoleId == customerRoleId));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var keyword = query.Trim();
            customerQuery = customerQuery.Where(user =>
                user.FullName.Contains(keyword) ||
                (user.Email != null && user.Email.Contains(keyword)) ||
                (user.PhoneNumber != null && user.PhoneNumber.Contains(keyword)));
        }

        var customers = await customerQuery
            .OrderBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .Take(100)
            .Select(user => new StaffCustomerListItemViewModel
            {
                CustomerId = user.Id,
                FullName = user.FullName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return View(new StaffCustomerIndexViewModel
        {
            Query = query,
            Customers = customers
        });
    }

    [HttpGet]
    public IActionResult Create() => View(new StaffCreateCustomerViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        StaffCreateCustomerViewModel model,
        CancellationToken cancellationToken)
    {
        // Format CCCD/GPLX lấy trực tiếp từ đúng ViewModel mà Customer KYC đang dùng.
        // Ở đây chỉ thêm các validate liên trường giống KycPackageController.
        await ValidateKycPackageAsync(model, cancellationToken);

        var accountName = NormalizePersonName(model.FullName);
        var documentName = NormalizePersonName(model.CitizenIdVerification.FullNameOnDocument);
        if (!string.IsNullOrWhiteSpace(accountName) &&
            !string.IsNullOrWhiteSpace(documentName) &&
            !string.Equals(accountName, documentName, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(
                nameof(model.FullName),
                "Họ tên tài khoản phải trùng họ tên trên CCCD đã đối chiếu tại quầy.");
            ModelState.AddModelError(
                "CitizenIdVerification.FullNameOnDocument",
                "Họ tên trên CCCD phải trùng họ tên tài khoản khách hàng.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var citizenIdNumber = model.CitizenIdVerification.DocumentNumber.Trim();
        var drivingLicenseNumber = model.DrivingLicenseVerification.DocumentNumber
            .Trim()
            .ToUpperInvariant();

        await ValidateDuplicateDocumentsAsync(
            citizenIdNumber,
            drivingLicenseNumber,
            cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var accountResult = await _accountService.RegisterCustomerAsync(
            new RegisterCustomerRequest(
                model.FullName,
                model.PhoneNumber,
                model.Email,
                model.Password),
            cancellationToken);

        if (!accountResult.Succeeded)
        {
            foreach (var error in accountResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        var normalizedEmail = model.Email.Trim().ToLowerInvariant();
        var customer = await _userManager.FindByEmailAsync(normalizedEmail);
        if (customer is null)
        {
            ModelState.AddModelError(
                string.Empty,
                "Đã tạo tài khoản nhưng không thể nạp lại khách hàng vừa tạo.");
            return View(model);
        }

        var newPaths = new List<string>();

        try
        {
            var citizen = model.CitizenIdVerification;
            var license = model.DrivingLicenseVerification;

            var citizenFrontPath = await SaveKycImageAsync(
                citizen.FrontImage!, customer.Id, newPaths, cancellationToken);
            var citizenBackPath = await SaveKycImageAsync(
                citizen.BackImage!, customer.Id, newPaths, cancellationToken);
            var licenseFrontPath = await SaveKycImageAsync(
                license.FrontImage!, customer.Id, newPaths, cancellationToken);
            var licenseBackPath = await SaveKycImageAsync(
                license.BackImage!, customer.Id, newPaths, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            // Kiểm tra lần hai trong transaction để tránh hai nhân viên cùng gán một số giấy tờ.
            var duplicateCitizen = await HasDuplicateDocumentNumberAsync(
                customer.Id,
                citizenIdNumber,
                DocumentTypes.CitizenId,
                DocumentTypes.CitizenIdBack,
                cancellationToken);
            var duplicateLicense = await HasDuplicateDocumentNumberAsync(
                customer.Id,
                drivingLicenseNumber,
                DocumentTypes.DrivingLicense,
                DocumentTypes.DrivingLicenseBack,
                cancellationToken);

            if (duplicateCitizen || duplicateLicense)
            {
                await transaction.RollbackAsync(cancellationToken);
                await CleanupCreatedCustomerAsync(customer, newPaths);
                ModelState.AddModelError(
                    string.Empty,
                    duplicateCitizen
                        ? "Số CCCD vừa được tài khoản khác sử dụng. Vui lòng kiểm tra lại."
                        : "Số GPLX vừa được tài khoản khác sử dụng. Vui lòng kiểm tra lại.");
                return View(model);
            }

            var createdAt = DateTime.UtcNow;
            var citizenFront = CreatePendingDocument(
                customer.Id,
                DocumentTypes.CitizenId,
                citizenIdNumber,
                citizen.ExpiryDate!.Value.Date,
                citizenFrontPath,
                createdAt);
            var citizenBack = CreatePendingDocument(
                customer.Id,
                DocumentTypes.CitizenIdBack,
                citizenIdNumber,
                citizen.ExpiryDate.Value.Date,
                citizenBackPath,
                createdAt);
            var licenseFront = CreatePendingDocument(
                customer.Id,
                DocumentTypes.DrivingLicense,
                drivingLicenseNumber,
                license.ExpiryDate!.Value.Date,
                licenseFrontPath,
                createdAt);
            var licenseBack = CreatePendingDocument(
                customer.Id,
                DocumentTypes.DrivingLicenseBack,
                drivingLicenseNumber,
                license.ExpiryDate.Value.Date,
                licenseBackPath,
                createdAt);

            _dbContext.CustomerDocuments.AddRange(
                citizenFront,
                citizenBack,
                licenseFront,
                licenseBack);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Metadata giống KycPackageController của Customer:
            // CCCD giữ họ tên + ngày sinh + giới tính; GPLX giữ cùng họ tên + hạng bằng.
            foreach (var document in new[] { citizenFront, citizenBack })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    citizen.FullNameOnDocument.Trim(),
                    citizen.DateOfBirth!.Value.Date,
                    citizen.Gender.Trim(),
                    issuedDate: null,
                    permanentAddress: null,
                    licenseClass: null,
                    cancellationToken);
            }

            foreach (var document in new[] { licenseFront, licenseBack })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    citizen.FullNameOnDocument.Trim(),
                    dateOfBirth: null,
                    gender: null,
                    issuedDate: null,
                    permanentAddress: null,
                    license.LicenseClass.Trim().ToUpperInvariant(),
                    cancellationToken);
            }

            // Không tự set Status=Verified nữa.
            // Đi qua đúng service xác minh mà Admin KYC đang sử dụng.
            var verifierId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException("Không xác định được người đang xác minh hồ sơ.");

            foreach (var documentId in new[]
                     {
                         citizenFront.CustomerDocumentId,
                         citizenBack.CustomerDocumentId,
                         licenseFront.CustomerDocumentId,
                         licenseBack.CustomerDocumentId
                     })
            {
                var verifyResult = await _documentService.VerifyAsync(
                    documentId,
                    verifierId,
                    cancellationToken);

                if (!verifyResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Không thể xác minh hồ sơ tại quầy: " +
                        string.Join("; ", verifyResult.Errors));
                }
            }

            // VerifyAsync sinh thông báo theo từng mặt giấy tờ. Với nghiệp vụ tại quầy,
            // gộp lại thành một thông báo để khách không nhận 4 thông báo giống nhau.
            var generatedNotifications = await _dbContext.Notifications
                .Where(notification =>
                    notification.UserId == customer.Id &&
                    notification.Title == "Giấy tờ đã được xác minh" &&
                    notification.CreatedAt >= createdAt.AddMinutes(-1))
                .ToListAsync(cancellationToken);

            if (generatedNotifications.Count > 0)
            {
                _dbContext.Notifications.RemoveRange(generatedNotifications);
            }

            _dbContext.Notifications.Add(new Notification
            {
                UserId = customer.Id,
                Title = "Hồ sơ tại quầy đã được xác minh",
                Message =
                    "SmartCar đã đối chiếu trực tiếp CCCD và GPLX bản gốc tại quầy. " +
                    "Khi tạo đơn, hệ thống vẫn kiểm tra giấy tờ phải còn hiệu lực đến ngày trả xe."
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _auditService.WriteAsync(
                verifierId,
                "StaffCreateWalkInCustomer",
                "CustomerKycPackage",
                customer.Id,
                $"Tạo Customer tại quầy cho {customer.FullName} ({normalizedEmail}); " +
                "hồ sơ dùng cùng cấu trúc KYC Customer và được xác minh qua IDocumentService sau khi đối chiếu bản gốc.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            TempData["SuccessMessage"] =
                "Đã tạo khách và xác minh CCCD/GPLX tại quầy. Có thể lập đơn thuê ngay.";

            return RedirectToAction(
                "CounterRental",
                "Staff",
                new { customerId = customer.Id });
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Không thể hoàn tất hồ sơ khách tại quầy cho {Email}.",
                normalizedEmail);

            await CleanupCreatedCustomerAsync(customer, newPaths);
            ModelState.AddModelError(
                string.Empty,
                "Không thể hoàn tất hồ sơ khách tại quầy. Tài khoản tạm tạo đã được thu hồi; vui lòng kiểm tra dữ liệu và thử lại.");
            return View(model);
        }
    }

    private async Task ValidateKycPackageAsync(
        StaffCreateCustomerViewModel model,
        CancellationToken cancellationToken)
    {
        var citizen = model.CitizenIdVerification;
        var license = model.DrivingLicenseVerification;

        await ValidateImageAsync(
            "CitizenIdVerification.FrontImage",
            citizen.FrontImage,
            cancellationToken);
        await ValidateImageAsync(
            "CitizenIdVerification.BackImage",
            citizen.BackImage,
            cancellationToken);
        await ValidateImageAsync(
            "DrivingLicenseVerification.FrontImage",
            license.FrontImage,
            cancellationToken);
        await ValidateImageAsync(
            "DrivingLicenseVerification.BackImage",
            license.BackImage,
            cancellationToken);

        if (await ImageFileValidator.HaveSameContentAsync(
                citizen.FrontImage,
                citizen.BackImage,
                cancellationToken))
        {
            ModelState.AddModelError(
                "CitizenIdVerification.BackImage",
                "Ảnh CCCD mặt trước và mặt sau phải là hai ảnh khác nhau.");
        }

        if (await ImageFileValidator.HaveSameContentAsync(
                license.FrontImage,
                license.BackImage,
                cancellationToken))
        {
            ModelState.AddModelError(
                "DrivingLicenseVerification.BackImage",
                "Ảnh GPLX mặt trước và mặt sau phải là hai ảnh khác nhau.");
        }

        if (citizen.DateOfBirth.HasValue &&
            citizen.DateOfBirth.Value.Date > DateTime.Today.AddYears(-18))
        {
            ModelState.AddModelError(
                "CitizenIdVerification.DateOfBirth",
                "Khách thuê xe phải đủ 18 tuổi.");
        }

        ValidateExpiryDate(
            "CCCD",
            "CitizenIdVerification.ExpiryDate",
            citizen.ExpiryDate);
        ValidateExpiryDate(
            "GPLX",
            "DrivingLicenseVerification.ExpiryDate",
            license.ExpiryDate);
    }

    private async Task ValidateImageAsync(
        string key,
        Microsoft.AspNetCore.Http.IFormFile? file,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(
            file,
            MaximumKycImageBytes,
            cancellationToken);

        if (error is not null)
        {
            ModelState.AddModelError(key, error);
        }
    }

    private void ValidateExpiryDate(
        string documentName,
        string key,
        DateTime? expiryDate)
    {
        if (expiryDate.HasValue && expiryDate.Value.Date < DateTime.Today)
        {
            ModelState.AddModelError(key, $"{documentName} đã hết hạn.");
        }
    }

    private async Task ValidateDuplicateDocumentsAsync(
        string citizenIdNumber,
        string drivingLicenseNumber,
        CancellationToken cancellationToken)
    {
        if (await HasDuplicateDocumentNumberAsync(
                excludedCustomerId: null,
                citizenIdNumber,
                DocumentTypes.CitizenId,
                DocumentTypes.CitizenIdBack,
                cancellationToken))
        {
            ModelState.AddModelError(
                "CitizenIdVerification.DocumentNumber",
                "Số CCCD này đã thuộc một tài khoản khác trong hệ thống.");
        }

        if (await HasDuplicateDocumentNumberAsync(
                excludedCustomerId: null,
                drivingLicenseNumber,
                DocumentTypes.DrivingLicense,
                DocumentTypes.DrivingLicenseBack,
                cancellationToken))
        {
            ModelState.AddModelError(
                "DrivingLicenseVerification.DocumentNumber",
                "Số GPLX này đã thuộc một tài khoản khác trong hệ thống.");
        }
    }

    private Task<bool> HasDuplicateDocumentNumberAsync(
        string? excludedCustomerId,
        string documentNumber,
        string frontType,
        string backType,
        CancellationToken cancellationToken)
    {
        return _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                (excludedCustomerId == null || document.CustomerId != excludedCustomerId) &&
                (document.DocumentType == frontType || document.DocumentType == backType) &&
                document.DocumentNumber == documentNumber,
                cancellationToken);
    }

    private async Task<string> SaveKycImageAsync(
        Microsoft.AspNetCore.Http.IFormFile file,
        string ownerId,
        ICollection<string> storedPaths,
        CancellationToken cancellationToken)
    {
        var path = await _secureDocumentStorage.SaveAsync(
            file,
            ownerId,
            cancellationToken);
        storedPaths.Add(path);
        return path;
    }

    private static CustomerDocument CreatePendingDocument(
        string customerId,
        string documentType,
        string documentNumber,
        DateTime expiryDate,
        string imagePath,
        DateTime createdAt)
    {
        return new CustomerDocument
        {
            CustomerId = customerId,
            DocumentType = documentType,
            DocumentNumber = documentNumber,
            ExpiryDate = expiryDate,
            ImagePath = imagePath,
            Status = DocumentStatus.Pending,
            RejectionReason = null,
            VerifiedBy = null,
            VerifiedAt = null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
    }

    private Task UpdateKycMetadataAsync(
        int documentId,
        string? fullNameOnDocument,
        DateTime? dateOfBirth,
        string? gender,
        DateTime? issuedDate,
        string? permanentAddress,
        string? licenseClass,
        CancellationToken cancellationToken)
    {
        return _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE [CustomerDocuments]
            SET [FullNameOnDocument] = {fullNameOnDocument},
                [DateOfBirth] = {dateOfBirth},
                [Gender] = {gender},
                [IssuedDate] = {issuedDate},
                [PermanentAddress] = {permanentAddress},
                [LicenseClass] = {licenseClass}
            WHERE [CustomerDocumentId] = {documentId}", cancellationToken);
    }

    private async Task CleanupCreatedCustomerAsync(
        ApplicationUser customer,
        IEnumerable<string> storedPaths)
    {
        foreach (var path in storedPaths)
        {
            try
            {
                _secureDocumentStorage.Delete(path);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Không thể xóa file KYC tạm {Path}.",
                    path);
            }
        }

        try
        {
            var currentCustomer = await _userManager.FindByIdAsync(customer.Id);
            if (currentCustomer is not null)
            {
                var deleteResult = await _userManager.DeleteAsync(currentCustomer);
                if (!deleteResult.Succeeded)
                {
                    _logger.LogWarning(
                        "Không thể thu hồi tài khoản tạm {CustomerId}: {Errors}",
                        customer.Id,
                        string.Join("; ", deleteResult.Errors.Select(error => error.Description)));
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Không thể thu hồi tài khoản tạm {CustomerId}.",
                customer.Id);
        }
    }

    private static string NormalizePersonName(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty)
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
