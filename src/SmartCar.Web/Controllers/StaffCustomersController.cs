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

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffCustomersController : Controller
{
    private readonly IAccountService _accountService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ISecureDocumentStorage _secureDocumentStorage;
    private readonly IUserBankAccountService _bankAccountService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<StaffCustomersController> _logger;

    public StaffCustomersController(
        IAccountService accountService,
        ApplicationDbContext dbContext,
        IAuditService auditService,
        ISecureDocumentStorage secureDocumentStorage,
        IUserBankAccountService bankAccountService,
        UserManager<ApplicationUser> userManager,
        ILogger<StaffCustomersController> logger)
    {
        _accountService = accountService;
        _dbContext = dbContext;
        _auditService = auditService;
        _secureDocumentStorage = secureDocumentStorage;
        _bankAccountService = bankAccountService;
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
                HasIdentityFace = user.IdentityFaceImagePath != null &&
                    user.IdentityFaceImagePath != "" &&
                    user.IdentityFaceCapturedAt.HasValue,
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
        // Dùng cùng validator với gói KYC do khách tự gửi để tránh hai bộ quy tắc khác nhau.
        // Nhân viên chỉ hỗ trợ nhập và đối chiếu bản gốc; quyền duyệt KYC thuộc quản trị viên.
        var kycPackage = new KycPackageSubmitViewModel
        {
            CitizenIdVerification = model.CitizenIdVerification,
            DrivingLicenseVerification = model.DrivingLicenseVerification,
            ConfirmSamePerson = model.ConfirmSamePerson
        };

        foreach (var validationError in await KycPackageValidator.ValidateAsync(
                     kycPackage,
                     requiredValidThrough: null,
                     cancellationToken))
        {
            ModelState.AddModelError(validationError.Key, validationError.Message);
        }

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

            // Walk-in phải hoàn tất luôn tài khoản nhận hoàn tại quầy; nếu bước này lỗi,
            // transaction KYC bị rollback và tài khoản tạm sẽ được thu hồi trong catch.
            await _bankAccountService.SaveDefaultAsync(
                customer.Id,
                model.BankAccount.BankCode,
                model.BankAccount.AccountNumber,
                model.BankAccount.AccountHolderName,
                cancellationToken);

            // Metadata dùng cùng cấu trúc với luồng khách tự gửi KYC.
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
                    cancellationToken: cancellationToken);
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
                    licenseClass: license.LicenseClass.Trim().ToUpperInvariant(),
                    cancellationToken: cancellationToken);
            }

            // Tuyệt đối không gọi IDocumentService.VerifyAsync ở đây.
            // Nhân viên chỉ tiếp nhận hồ sơ và đối chiếu bản gốc; cả 4 giấy tờ giữ trạng thái Pending
            // để quản trị viên mở ảnh, đối chiếu dữ liệu rồi duyệt/từ chối trong luồng Admin KYC.
            var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException("Không xác định được nhân viên đang tiếp nhận hồ sơ.");

            _dbContext.Notifications.Add(new Notification
            {
                UserId = customer.Id,
                Title = "Hồ sơ giấy tờ đã được tiếp nhận",
                Message =
                    "Nhân viên đã hỗ trợ tiếp nhận CCCD/GPLX và tài khoản nhận hoàn tại quầy. " +
                    "Cần hoàn tất ảnh khuôn mặt trực tiếp tại quầy trước khi hồ sơ được chuyển sang hàng chờ quản trị viên duyệt."
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Audit tổng hợp là phụ trợ; lỗi audit sau commit không được xóa ngược khách đã tạo.
            try
            {
                await _auditService.WriteAsync(
                    staffId,
                    "StaffSubmitWalkInCustomerKyc",
                    "CustomerKycPackage",
                    customer.Id,
                    $"Tạo tài khoản khách tại quầy cho {customer.FullName} ({normalizedEmail}); " +
                    $"đã lưu tài khoản nhận hoàn tại ngân hàng {model.BankAccount.BankCode}; " +
                    "nhân viên đã đối chiếu bản gốc và gửi 4 giấy tờ ở trạng thái Chờ xử lý để quản trị viên duyệt.",
                    ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken: cancellationToken);
            }
            catch (Exception auditException)
            {
                _logger.LogWarning(
                    auditException,
                    "Không thể ghi audit tổng hợp khi tiếp nhận KYC tại quầy {CustomerId}.",
                    customer.Id);
            }

            TempData["SuccessMessage"] =
                "Đã tạo tài khoản, lưu tài khoản nhận hoàn và tiếp nhận CCCD/GPLX. Bước tiếp theo: chụp khuôn mặt khách trực tiếp tại quầy.";

            return RedirectToAction(nameof(FaceCapture), new { customerId = customer.Id });
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

    [HttpGet]
    public async Task<IActionResult> FaceCapture(
        string customerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return BadRequest();
        }

        var customer = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == customerId && user.IsActive)
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
                HasFace = user.IdentityFaceImagePath != null &&
                          user.IdentityFaceImagePath != "" &&
                          user.IdentityFaceCapturedAt.HasValue
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (customer is null ||
            !await _userManager.IsInRoleAsync(
                await _userManager.FindByIdAsync(customerId)
                    ?? throw new InvalidOperationException("Không tìm thấy khách hàng."),
                RoleNames.Customer))
        {
            return NotFound();
        }

        var requiredTypes = new[]
        {
            DocumentTypes.CitizenId,
            DocumentTypes.CitizenIdBack,
            DocumentTypes.DrivingLicense,
            DocumentTypes.DrivingLicenseBack
        };
        var pendingTypes = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == customerId &&
                document.Status == DocumentStatus.Pending &&
                requiredTypes.Contains(document.DocumentType))
            .Select(document => document.DocumentType)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (pendingTypes.Count != requiredTypes.Length)
        {
            TempData["ErrorMessage"] =
                "Khách chưa có đủ 4 giấy tờ KYC đang chờ xử lý. Hãy hoàn tất hồ sơ CCCD/GPLX trước.";
            return RedirectToAction(nameof(Index), new { query = customer.Email });
        }

        if (customer.HasFace)
        {
            TempData["SuccessMessage"] =
                "Khách đã có ảnh mặt KYC trực tiếp. Hồ sơ đã sẵn sàng để Admin duyệt.";
            return RedirectToAction(nameof(Index), new { query = customer.Email });
        }

        ViewBag.CustomerName = customer.FullName;
        ViewBag.CustomerEmail = customer.Email;
        return View(new IdentityCaptureWidgetViewModel
        {
            Purpose = IdentityCapturePurposes.Kyc,
            HiddenFieldName = "FaceCaptureSessionId",
            CustomerId = customer.Id,
            Title = "Ảnh khuôn mặt khách tại quầy",
            HelpText = "Chụp trực tiếp bằng camera máy quầy hoặc QR sang điện thoại. Ảnh này là bằng chứng KYC và không được chọn từ thư viện trong luồng bình thường.",
            AllowStaffFallback = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteFaceCapture(
        string customerId,
        Guid? faceCaptureSessionId,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(customerId) || !faceCaptureSessionId.HasValue)
        {
            TempData["ErrorMessage"] = "Chưa có ảnh mặt KYC hợp lệ.";
            return RedirectToAction(nameof(FaceCapture), new { customerId });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var customer = await _dbContext.Users
            .FirstOrDefaultAsync(user => user.Id == customerId && user.IsActive, cancellationToken);
        var session = await _dbContext.Set<IdentityCaptureSession>()
            .FirstOrDefaultAsync(item =>
                item.IdentityCaptureSessionId == faceCaptureSessionId.Value &&
                item.Purpose == IdentityCapturePurposes.Kyc &&
                item.TargetCustomerId == customerId &&
                item.CreatedByUserId == staffId,
                cancellationToken);

        var now = DateTime.UtcNow;
        var validSession = customer is not null &&
                           session is not null &&
                           session.CompletedAt.HasValue &&
                           !session.ConsumedAt.HasValue &&
                           session.ExpiresAt > now &&
                           !string.IsNullOrWhiteSpace(session.ImagePath) &&
                           IdentityCaptureMethods.All.Contains(
                               session.CaptureMethod ?? string.Empty,
                               StringComparer.Ordinal);

        if (!validSession)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Phiên ảnh mặt không hợp lệ, đã hết hạn, đã dùng hoặc không thuộc đúng khách/nhân viên. Vui lòng chụp lại.";
            return RedirectToAction(nameof(FaceCapture), new { customerId });
        }

        var oldPath = customer!.IdentityFaceImagePath;
        customer.IdentityFaceImagePath = session!.ImagePath;
        customer.IdentityFaceCapturedAt = session.CompletedAt;
        customer.IdentityFaceCaptureMethod = session.CaptureMethod;
        session.ConsumedAt = now;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = customer.Id,
            Title = "Hồ sơ KYC đã đủ dữ liệu để duyệt",
            Message =
                "Đã hoàn tất ảnh khuôn mặt trực tiếp cùng CCCD/GPLX tại quầy. Hồ sơ đang chờ Quản trị viên kiểm tra và duyệt."
        });

        var adminRoleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(adminRoleId))
        {
            var adminIds = await _dbContext.UserRoles
                .Where(item => item.RoleId == adminRoleId)
                .Select(item => item.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var title = $"Hồ sơ KYC chờ duyệt|{customer.Id}";
            var stale = await _dbContext.Notifications
                .Where(item =>
                    adminIds.Contains(item.UserId) &&
                    !item.IsRead &&
                    item.Title == title)
                .ToListAsync(cancellationToken);
            if (stale.Count > 0)
            {
                _dbContext.Notifications.RemoveRange(stale);
            }

            foreach (var adminId in adminIds)
            {
                _dbContext.Notifications.Add(new Notification
                {
                    UserId = adminId,
                    Title = title,
                    Message =
                        $"{customer.FullName} đã hoàn tất CCCD, GPLX và ảnh mặt trực tiếp tại quầy. Hãy mở hồ sơ để đối chiếu và ra quyết định xác minh."
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(oldPath) &&
            !string.Equals(oldPath, customer.IdentityFaceImagePath, StringComparison.OrdinalIgnoreCase))
        {
            _secureDocumentStorage.Delete(oldPath);
        }

        await _auditService.WriteAsync(
            staffId,
            "StaffCompleteWalkInKycFace",
            nameof(IdentityCaptureSession),
            session.IdentityCaptureSessionId.ToString(),
            $"Hoàn tất ảnh mặt KYC trực tiếp tại quầy cho khách {customer.FullName} ({customer.Id}); phương thức {session.CaptureMethod}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã hoàn tất ảnh mặt KYC. Hồ sơ hiện đã đủ dữ liệu để Admin mở và duyệt.";
        return RedirectToAction(nameof(Index), new { query = customer.Email });
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
