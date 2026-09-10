using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Accounts;
using SmartCar.Application.Features.Audits;
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
    private readonly IAuditService _auditService;
    private readonly ISecureDocumentStorage _secureDocumentStorage;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<StaffCustomersController> _logger;

    public StaffCustomersController(
        IAccountService accountService,
        ApplicationDbContext dbContext,
        IAuditService auditService,
        ISecureDocumentStorage secureDocumentStorage,
        UserManager<ApplicationUser> userManager,
        ILogger<StaffCustomersController> logger)
    {
        _accountService = accountService;
        _dbContext = dbContext;
        _auditService = auditService;
        _secureDocumentStorage = secureDocumentStorage;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Create() => View(new StaffCreateCustomerViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        StaffCreateCustomerViewModel model,
        CancellationToken cancellationToken)
    {
        ValidateDocumentDates(model);
        await ValidateKycImagesAsync(model, cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var citizenIdNumber = model.CitizenIdNumber.Trim();
        var drivingLicenseNumber = model.DrivingLicenseNumber.Trim().ToUpperInvariant();

        var citizenIdExists = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                (document.DocumentType == DocumentTypes.CitizenId ||
                 document.DocumentType == DocumentTypes.CitizenIdBack) &&
                document.DocumentNumber == citizenIdNumber,
                cancellationToken);

        if (citizenIdExists)
        {
            ModelState.AddModelError(
                nameof(model.CitizenIdNumber),
                "Số CCCD này đã thuộc một tài khoản khác trong hệ thống.");
        }

        var drivingLicenseExists = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .AnyAsync(document =>
                (document.DocumentType == DocumentTypes.DrivingLicense ||
                 document.DocumentType == DocumentTypes.DrivingLicenseBack) &&
                document.DocumentNumber == drivingLicenseNumber,
                cancellationToken);

        if (drivingLicenseExists)
        {
            ModelState.AddModelError(
                nameof(model.DrivingLicenseNumber),
                "Số GPLX này đã thuộc một tài khoản khác trong hệ thống.");
        }

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
                "Đã tạo tài khoản nhưng không thể nạp lại thông tin khách. Vui lòng thử lại.");
            return View(model);
        }

        var storedPaths = new List<string>();

        try
        {
            var citizenFrontPath = await SaveKycImageAsync(
                model.CitizenIdFrontImage!, customer.Id, storedPaths, cancellationToken);
            var citizenBackPath = await SaveKycImageAsync(
                model.CitizenIdBackImage!, customer.Id, storedPaths, cancellationToken);
            var licenseFrontPath = await SaveKycImageAsync(
                model.DrivingLicenseFrontImage!, customer.Id, storedPaths, cancellationToken);
            var licenseBackPath = await SaveKycImageAsync(
                model.DrivingLicenseBackImage!, customer.Id, storedPaths, cancellationToken);

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            // Kiểm tra lại trong transaction để giảm nguy cơ hai nhân viên cùng tạo một bộ giấy tờ.
            var duplicateCitizen = await _dbContext.CustomerDocuments
                .AnyAsync(document =>
                    document.CustomerId != customer.Id &&
                    (document.DocumentType == DocumentTypes.CitizenId ||
                     document.DocumentType == DocumentTypes.CitizenIdBack) &&
                    document.DocumentNumber == citizenIdNumber,
                    cancellationToken);

            var duplicateLicense = await _dbContext.CustomerDocuments
                .AnyAsync(document =>
                    document.CustomerId != customer.Id &&
                    (document.DocumentType == DocumentTypes.DrivingLicense ||
                     document.DocumentType == DocumentTypes.DrivingLicenseBack) &&
                    document.DocumentNumber == drivingLicenseNumber,
                    cancellationToken);

            if (duplicateCitizen || duplicateLicense)
            {
                await transaction.RollbackAsync(cancellationToken);
                await CleanupCreatedCustomerAsync(customer, storedPaths);

                ModelState.AddModelError(
                    string.Empty,
                    duplicateCitizen
                        ? "CCCD vừa được tài khoản khác sử dụng. Không thể xác minh khách tại quầy."
                        : "GPLX vừa được tài khoản khác sử dụng. Không thể xác minh khách tại quầy.");
                return View(model);
            }

            var verifiedAt = DateTime.UtcNow;
            var verifierId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException("Không xác định được tài khoản nhân viên đang thao tác.");

            var citizenFront = CreateVerifiedDocument(
                customer.Id,
                DocumentTypes.CitizenId,
                citizenIdNumber,
                model.CitizenIdExpiryDate!.Value.Date,
                citizenFrontPath,
                verifierId,
                verifiedAt);

            var citizenBack = CreateVerifiedDocument(
                customer.Id,
                DocumentTypes.CitizenIdBack,
                citizenIdNumber,
                model.CitizenIdExpiryDate!.Value.Date,
                citizenBackPath,
                verifierId,
                verifiedAt);

            var licenseFront = CreateVerifiedDocument(
                customer.Id,
                DocumentTypes.DrivingLicense,
                drivingLicenseNumber,
                model.DrivingLicenseExpiryDate!.Value.Date,
                licenseFrontPath,
                verifierId,
                verifiedAt);

            var licenseBack = CreateVerifiedDocument(
                customer.Id,
                DocumentTypes.DrivingLicenseBack,
                drivingLicenseNumber,
                model.DrivingLicenseExpiryDate!.Value.Date,
                licenseBackPath,
                verifierId,
                verifiedAt);

            _dbContext.CustomerDocuments.AddRange(
                citizenFront,
                citizenBack,
                licenseFront,
                licenseBack);

            await _dbContext.SaveChangesAsync(cancellationToken);

            var fullName = customer.FullName.Trim();
            var dateOfBirth = model.CitizenIdDateOfBirth!.Value.Date;
            var citizenIssuedDate = model.CitizenIdIssuedDate!.Value.Date;
            var licenseIssuedDate = model.DrivingLicenseIssuedDate!.Value.Date;
            var gender = model.CitizenIdGender.Trim();
            var permanentAddress = model.CitizenIdPermanentAddress.Trim();
            var licenseClass = model.DrivingLicenseClass.Trim().ToUpperInvariant();

            foreach (var document in new[] { citizenFront, citizenBack })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    fullName,
                    dateOfBirth,
                    gender,
                    citizenIssuedDate,
                    permanentAddress,
                    null,
                    cancellationToken);
            }

            foreach (var document in new[] { licenseFront, licenseBack })
            {
                await UpdateKycMetadataAsync(
                    document.CustomerDocumentId,
                    fullName,
                    dateOfBirth,
                    gender,
                    licenseIssuedDate,
                    permanentAddress,
                    licenseClass,
                    cancellationToken);
            }

            _dbContext.Notifications.Add(new Notification
            {
                UserId = customer.Id,
                Title = "Hồ sơ tại quầy đã được xác minh",
                Message =
                    "SmartCar đã đối chiếu CCCD và GPLX bản gốc tại quầy. " +
                    "Tài khoản có thể được dùng để lập đơn thuê nếu giấy tờ còn hiệu lực đến hết chuyến."
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            try
            {
                await _auditService.WriteAsync(
                    verifierId,
                    "StaffCreateWalkInCustomer",
                    "CustomerKycPackage",
                    customer.Id,
                    $"Tạo Customer tại quầy cho {customer.FullName} ({normalizedEmail}); " +
                    "đã trực tiếp đối chiếu CCCD/GPLX bản gốc và xác minh 4 mặt giấy tờ.",
                    ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken: cancellationToken);
            }
            catch (Exception auditException)
            {
                _logger.LogWarning(
                    auditException,
                    "Không thể ghi audit sau khi tạo khách tại quầy {CustomerId}.",
                    customer.Id);
            }

            TempData["SuccessMessage"] =
                "Đã tạo tài khoản và xác minh CCCD/GPLX tại quầy. Khách đã sẵn sàng để lập đơn thuê.";

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

            await CleanupCreatedCustomerAsync(customer, storedPaths);
            ModelState.AddModelError(
                string.Empty,
                "Không thể hoàn tất hồ sơ khách tại quầy. Tài khoản tạm tạo đã được thu hồi; vui lòng kiểm tra dữ liệu và thử lại.");
            return View(model);
        }
    }

    private void ValidateDocumentDates(StaffCreateCustomerViewModel model)
    {
        var today = DateTime.Today;

        if (model.CitizenIdDateOfBirth.HasValue &&
            model.CitizenIdDateOfBirth.Value.Date > today.AddYears(-18))
        {
            ModelState.AddModelError(
                nameof(model.CitizenIdDateOfBirth),
                "Khách thuê xe phải đủ 18 tuổi.");
        }

        ValidateIssueAndExpiry(
            model.CitizenIdIssuedDate,
            model.CitizenIdExpiryDate,
            nameof(model.CitizenIdIssuedDate),
            nameof(model.CitizenIdExpiryDate),
            "CCCD");

        ValidateIssueAndExpiry(
            model.DrivingLicenseIssuedDate,
            model.DrivingLicenseExpiryDate,
            nameof(model.DrivingLicenseIssuedDate),
            nameof(model.DrivingLicenseExpiryDate),
            "GPLX");
    }

    private void ValidateIssueAndExpiry(
        DateTime? issuedDate,
        DateTime? expiryDate,
        string issuedField,
        string expiryField,
        string documentName)
    {
        var today = DateTime.Today;

        if (issuedDate.HasValue && issuedDate.Value.Date > today)
        {
            ModelState.AddModelError(
                issuedField,
                $"Ngày cấp {documentName} không được sau ngày hiện tại.");
        }

        if (expiryDate.HasValue && expiryDate.Value.Date < today)
        {
            ModelState.AddModelError(
                expiryField,
                $"{documentName} đã hết hạn.");
        }

        if (issuedDate.HasValue && expiryDate.HasValue &&
            expiryDate.Value.Date <= issuedDate.Value.Date)
        {
            ModelState.AddModelError(
                expiryField,
                $"Ngày hết hạn {documentName} phải sau ngày cấp.");
        }
    }

    private async Task ValidateKycImagesAsync(
        StaffCreateCustomerViewModel model,
        CancellationToken cancellationToken)
    {
        await ValidateImageFieldAsync(
            model.CitizenIdFrontImage,
            nameof(model.CitizenIdFrontImage),
            cancellationToken);
        await ValidateImageFieldAsync(
            model.CitizenIdBackImage,
            nameof(model.CitizenIdBackImage),
            cancellationToken);
        await ValidateImageFieldAsync(
            model.DrivingLicenseFrontImage,
            nameof(model.DrivingLicenseFrontImage),
            cancellationToken);
        await ValidateImageFieldAsync(
            model.DrivingLicenseBackImage,
            nameof(model.DrivingLicenseBackImage),
            cancellationToken);

        if (await ImageFileValidator.HaveSameContentAsync(
                model.CitizenIdFrontImage,
                model.CitizenIdBackImage,
                cancellationToken))
        {
            ModelState.AddModelError(
                nameof(model.CitizenIdBackImage),
                "Ảnh mặt trước và mặt sau CCCD không được là cùng một file.");
        }

        if (await ImageFileValidator.HaveSameContentAsync(
                model.DrivingLicenseFrontImage,
                model.DrivingLicenseBackImage,
                cancellationToken))
        {
            ModelState.AddModelError(
                nameof(model.DrivingLicenseBackImage),
                "Ảnh mặt trước và mặt sau GPLX không được là cùng một file.");
        }
    }

    private async Task ValidateImageFieldAsync(
        Microsoft.AspNetCore.Http.IFormFile? file,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(
            file,
            MaximumKycImageBytes,
            cancellationToken);

        if (error is not null)
        {
            ModelState.AddModelError(fieldName, error);
        }
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

    private static CustomerDocument CreateVerifiedDocument(
        string customerId,
        string documentType,
        string documentNumber,
        DateTime expiryDate,
        string imagePath,
        string verifierId,
        DateTime verifiedAt)
    {
        return new CustomerDocument
        {
            CustomerId = customerId,
            DocumentType = documentType,
            DocumentNumber = documentNumber,
            ExpiryDate = expiryDate,
            ImagePath = imagePath,
            Status = DocumentStatus.Verified,
            RejectionReason = null,
            VerifiedBy = verifierId,
            VerifiedAt = verifiedAt,
            CreatedAt = verifiedAt,
            UpdatedAt = verifiedAt
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
}
