using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminStaffController : Controller
{
    private const string EmployeeCodePrefix = "SC-NV";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public AdminStaffController(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? query,
        string? status,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? "all"
            : status.Trim().ToLowerInvariant();

        if (normalizedStatus is not ("all" or "active" or "inactive"))
        {
            normalizedStatus = "all";
        }

        var users = await _userManager.GetUsersInRoleAsync(RoleNames.Staff);
        var totalStaffCount = users.Count;
        var activeStaffCount = users.Count(user => user.IsActive);
        var inactiveStaffCount = totalStaffCount - activeStaffCount;
        var pendingFirstPasswordChangeCount = users.Count(user => user.MustChangePassword);

        IEnumerable<ApplicationUser> filtered = users;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var keyword = query.Trim();
            filtered = filtered.Where(user =>
                (!string.IsNullOrWhiteSpace(user.EmployeeCode) && user.EmployeeCode.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                user.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(user.Email) && user.Email.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(user.PhoneNumber) && user.PhoneNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(user.CitizenIdNumber) && user.CitizenIdNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        filtered = normalizedStatus switch
        {
            "active" => filtered.Where(user => user.IsActive),
            "inactive" => filtered.Where(user => !user.IsActive),
            _ => filtered
        };

        var items = new List<AdminStaffListItemViewModel>();
        foreach (var user in filtered
                     .OrderByDescending(user => user.IsActive)
                     .ThenBy(user => string.IsNullOrWhiteSpace(user.EmployeeCode) ? "~" : user.EmployeeCode)
                     .ThenBy(user => user.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(new AdminStaffListItemViewModel
            {
                UserId = user.Id,
                EmployeeCode = user.EmployeeCode ?? string.Empty,
                FullName = user.FullName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                CitizenIdNumber = user.CitizenIdNumber,
                IsActive = user.IsActive,
                HasPassword = await _userManager.HasPasswordAsync(user),
                MustChangePassword = user.MustChangePassword,
                CreatedAt = user.CreatedAt
            });
        }

        return View(new AdminStaffIndexViewModel
        {
            Query = query,
            Status = normalizedStatus,
            TotalStaffCount = totalStaffCount,
            ActiveStaffCount = activeStaffCount,
            InactiveStaffCount = inactiveStaffCount,
            PendingFirstPasswordChangeCount = pendingFirstPasswordChangeCount,
            Staff = items
        });
    }

    [HttpGet]
    public IActionResult Create() => View(new AdminStaffCreateViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        AdminStaffCreateViewModel model,
        CancellationToken cancellationToken)
    {
        var fullName = NormalizeFullName(model.FullName);
        var email = (model.Email ?? string.Empty).Trim().ToLowerInvariant();
        var phoneNumber = NormalizePhoneNumber(model.PhoneNumber);
        var citizenIdNumber = (model.CitizenIdNumber ?? string.Empty).Trim();

        model.FullName = fullName;
        model.Email = email;
        model.PhoneNumber = phoneNumber;
        model.CitizenIdNumber = citizenIdNumber;

        ValidateFullName(fullName, nameof(model.FullName));
        if (!model.IdentityDocumentChecked)
        {
            ModelState.AddModelError(
                nameof(model.IdentityDocumentChecked),
                "Quản trị viên phải đối chiếu CCCD bản gốc trước khi tạo tài khoản nhân viên.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await ValidateStaffInputAsync(
            email,
            phoneNumber,
            citizenIdNumber,
            excludedUserId: null,
            cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Serializable giúp hai Admin tạo nhân viên cùng lúc không cùng lấy một mã SC-NVxxx.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var employeeCode = await GenerateEmployeeCodeAsync(cancellationToken);
        var temporaryPassword = model.TemporaryPassword;
        var adminId = CurrentUserId();
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            FullName = fullName,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            PhoneNumber = phoneNumber,
            EmployeeCode = employeeCode,
            CitizenIdNumber = citizenIdNumber,
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = adminId,
            VerifiedByUserId = adminId,
            VerifiedAt = now,
            MustChangePassword = true
        };

        var createResult = await AccountInputGuard.SaveAsync(() => _userManager.CreateAsync(user, temporaryPassword));
        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            AddIdentityErrors(createResult);
            return View(model);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, RoleNames.Staff);
        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            AddIdentityErrors(roleResult);
            return View(model);
        }

        await transaction.CommitAsync(cancellationToken);

        await WriteAuditAsync(
            "CreateStaffAccount",
            user.Id,
            $"Tạo nhân viên {employeeCode} - {fullName}, đã đối chiếu CCCD bản gốc, gán role Staff và yêu cầu đổi mật khẩu ở lần đăng nhập đầu tiên.",
            cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã tạo nhân viên {employeeCode}. Nhân viên đăng nhập bằng email và mật khẩu do Quản trị viên đặt; hệ thống sẽ bắt buộc đổi mật khẩu ở lần đăng nhập đầu tiên.";

        return RedirectToAction(nameof(Details), new { id = user.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        string id,
        CancellationToken cancellationToken)
    {
        var user = await GetStaffAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        return View(await BuildDetailsAsync(user, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevealCitizenId(
        string id,
        CancellationToken cancellationToken)
    {
        var user = await GetStaffAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(user.CitizenIdNumber))
        {
            return BadRequest(new { message = "Nhân viên chưa có số CCCD." });
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";

        await WriteAuditAsync(
            "RevealStaffCitizenId",
            user.Id,
            $"Quản trị viên xem đầy đủ CCCD của nhân viên {user.EmployeeCode ?? user.Email}; CCCD kết thúc bằng {LastFour(user.CitizenIdNumber)}.",
            cancellationToken);

        return Json(new { citizenId = user.CitizenIdNumber });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var user = await GetStaffAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        return View(new AdminStaffEditViewModel
        {
            UserId = user.Id,
            EmployeeCode = user.EmployeeCode ?? string.Empty,
            FullName = user.FullName,
            Email = user.Email ?? string.Empty,
            PhoneNumber = NormalizePhoneNumber(user.PhoneNumber ?? string.Empty),
            CitizenIdNumber = user.CitizenIdNumber ?? string.Empty
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        AdminStaffEditViewModel model,
        CancellationToken cancellationToken)
    {
        var user = await GetStaffAsync(model.UserId);
        if (user is null)
        {
            return NotFound();
        }

        model.EmployeeCode = user.EmployeeCode ?? string.Empty;
        var fullName = NormalizeFullName(model.FullName);
        var email = (model.Email ?? string.Empty).Trim().ToLowerInvariant();
        var phoneNumber = NormalizePhoneNumber(model.PhoneNumber);
        var citizenIdNumber = (model.CitizenIdNumber ?? string.Empty).Trim();
        var citizenIdChanged = !string.Equals(
            user.CitizenIdNumber?.Trim(),
            citizenIdNumber,
            StringComparison.Ordinal);

        model.FullName = fullName;
        model.Email = email;
        model.PhoneNumber = phoneNumber;
        model.CitizenIdNumber = citizenIdNumber;

        ValidateFullName(fullName, nameof(model.FullName));
        if (citizenIdChanged && !model.IdentityDocumentChecked)
        {
            ModelState.AddModelError(
                nameof(model.IdentityDocumentChecked),
                "Khi thay đổi CCCD, Quản trị viên phải đối chiếu lại CCCD bản gốc của nhân viên.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await ValidateStaffInputAsync(
            email,
            phoneNumber,
            citizenIdNumber,
            user.Id,
            cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        user.FullName = fullName;
        user.Email = email;
        user.UserName = email;
        user.EmailConfirmed = true;
        user.PhoneNumber = phoneNumber;
        user.CitizenIdNumber = citizenIdNumber;

        if (citizenIdChanged)
        {
            user.VerifiedByUserId = CurrentUserId();
            user.VerifiedAt = DateTime.UtcNow;
        }

        var updateResult = await AccountInputGuard.SaveAsync(() => _userManager.UpdateAsync(user));
        if (!updateResult.Succeeded)
        {
            AddIdentityErrors(updateResult);
            return View(model);
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await WriteAuditAsync(
            "UpdateStaffAccount",
            user.Id,
            citizenIdChanged
                ? $"Cập nhật hồ sơ nhân viên {user.EmployeeCode ?? user.Email} - {fullName} và đối chiếu lại CCCD bản gốc."
                : $"Cập nhật hồ sơ nhân viên {user.EmployeeCode ?? user.Email} - {fullName}.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật thông tin nhân viên.";
        return RedirectToAction(nameof(Details), new { id = user.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> LockStaff(string id, CancellationToken cancellationToken) =>
        SetActiveStatusAsync(id, false, cancellationToken);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UnlockStaff(string id, CancellationToken cancellationToken) =>
        SetActiveStatusAsync(id, true, cancellationToken);

    private async Task<IActionResult> SetActiveStatusAsync(
        string id, bool isActive, CancellationToken cancellationToken)
    {
        var user = await GetStaffAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (user.IsActive == isActive)
        {
            TempData["SuccessMessage"] = isActive ? "Tài khoản đã được kích hoạt." : "Tài khoản đã bị khóa.";
            return RedirectToAction(nameof(Details), new { id });
        }

        user.IsActive = isActive;
        var updateResult = await AccountInputGuard.SaveAsync(() => _userManager.UpdateAsync(user));
        if (!updateResult.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", updateResult.Errors.Select(TranslateIdentityError));
            return RedirectToAction(nameof(Details), new { id });
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await WriteAuditAsync(
            user.IsActive ? "ActivateStaffAccount" : "DeactivateStaffAccount",
            user.Id,
            user.IsActive
                ? $"Kích hoạt lại tài khoản nhân viên {user.EmployeeCode ?? user.Email}."
                : $"Khóa tài khoản nhân viên {user.EmployeeCode ?? user.Email}; phiên đăng nhập hiện tại sẽ bị kết thúc ở request tiếp theo và dữ liệu lịch sử được giữ nguyên.",
            cancellationToken);

        TempData["SuccessMessage"] = user.IsActive
            ? "Đã kích hoạt lại tài khoản nhân viên."
            : "Đã khóa tài khoản nhân viên. Phiên đang đăng nhập sẽ bị kết thúc ngay khi nhân viên thao tác tiếp.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<ApplicationUser?> GetStaffAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var user = await _userManager.FindByIdAsync(id);
        return user is not null && await _userManager.IsInRoleAsync(user, RoleNames.Staff)
            ? user
            : null;
    }

    private async Task<AdminStaffDetailsViewModel> BuildDetailsAsync(
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        var ids = new[] { user.CreatedByUserId, user.VerifiedByUserId }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .Cast<string>()
            .ToArray();

        var names = ids.Length == 0
            ? new Dictionary<string, string>()
            : await _dbContext.Users
                .AsNoTracking()
                .Where(item => ids.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.FullName, cancellationToken);

        return new AdminStaffDetailsViewModel
        {
            UserId = user.Id,
            EmployeeCode = user.EmployeeCode ?? string.Empty,
            FullName = user.FullName,
            Email = user.Email ?? string.Empty,
            PhoneNumber = user.PhoneNumber,
            MaskedCitizenIdNumber = MaskCitizenId(user.CitizenIdNumber),
            HasCitizenIdNumber = !string.IsNullOrWhiteSpace(user.CitizenIdNumber),
            IsActive = user.IsActive,
            HasPassword = await _userManager.HasPasswordAsync(user),
            MustChangePassword = user.MustChangePassword,
            CreatedAt = user.CreatedAt,
            CreatedByName = user.CreatedByUserId is not null && names.TryGetValue(user.CreatedByUserId, out var creator)
                ? creator
                : null,
            VerifiedAt = user.VerifiedAt,
            VerifiedByName = user.VerifiedByUserId is not null && names.TryGetValue(user.VerifiedByUserId, out var verifier)
                ? verifier
                : null
        };
    }

    private async Task ValidateStaffInputAsync(
        string email,
        string phoneNumber,
        string citizenIdNumber,
        string? excludedUserId,
        CancellationToken cancellationToken)
    {
        var existingEmail = await _userManager.FindByEmailAsync(email);
        if (existingEmail is not null && existingEmail.Id != excludedUserId)
        {
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.Email), "Email này đã được sử dụng bởi tài khoản khác.");
        }

        var staffRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Staff)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (await AccountInputGuard.PhoneExistsAsync(_userManager, phoneNumber, excludedUserId, cancellationToken))
        {
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.PhoneNumber), AccountInputGuard.DuplicatePhoneMessage);
        }

        if (citizenIdNumber.Length == 12 && citizenIdNumber.All(char.IsDigit) && !string.IsNullOrWhiteSpace(staffRoleId))
        {
            var staffCitizenExists = await _userManager.Users.AnyAsync(
                user => user.Id != excludedUserId &&
                        user.CitizenIdNumber == citizenIdNumber &&
                        _dbContext.UserRoles.Any(userRole =>
                            userRole.UserId == user.Id &&
                            userRole.RoleId == staffRoleId),
                cancellationToken);

            if (staffCitizenExists)
            {
                ModelState.AddModelError(
                    nameof(AdminStaffCreateViewModel.CitizenIdNumber),
                    "Số CCCD này đã được gán cho tài khoản nhân viên khác.");
            }
        }
    }

    private void ValidateFullName(string fullName, string key)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return;
        }

        if (fullName.Any(ch => !char.IsLetter(ch) && ch != ' '))
        {
            ModelState.AddModelError(key, "Họ và tên chỉ được chứa chữ cái và khoảng trắng, không được chứa số hoặc ký tự đặc biệt.");
        }
    }

    private async Task<string> GenerateEmployeeCodeAsync(CancellationToken cancellationToken)
    {
        var existingCodes = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.EmployeeCode != null && user.EmployeeCode.StartsWith(EmployeeCodePrefix))
            .Select(user => user.EmployeeCode!)
            .ToListAsync(cancellationToken);

        var maxNumber = 0;
        foreach (var code in existingCodes)
        {
            var numberPart = code[EmployeeCodePrefix.Length..];
            if (int.TryParse(numberPart, out var number) && number > maxNumber)
            {
                maxNumber = number;
            }
        }

        var nextNumber = maxNumber + 1;
        string nextCode;
        do
        {
            nextCode = $"{EmployeeCodePrefix}{nextNumber:000}";
            nextNumber++;
        }
        while (await _dbContext.Users.AsNoTracking().AnyAsync(user => user.EmployeeCode == nextCode, cancellationToken));

        return nextCode;
    }

    private async Task WriteAuditAsync(
        string action,
        string staffId,
        string description,
        CancellationToken cancellationToken)
    {
        await _auditService.WriteAsync(
            CurrentUserId(),
            action,
            "StaffAccount",
            staffId,
            description,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }

    private void AddIdentityErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, TranslateIdentityError(error));
        }
    }

    private static string TranslateIdentityError(IdentityError error) => error.Code switch
    {
        "DuplicateAccountData" => error.Description,
        "DuplicateEmail" => "Email này đã được sử dụng.",
        "DuplicateUserName" => "Email đăng nhập này đã được sử dụng.",
        "InvalidEmail" => "Email không đúng định dạng.",
        "InvalidUserName" => "Email đăng nhập chứa ký tự không hợp lệ.",
        "PasswordTooShort" => "Mật khẩu phải có ít nhất 8 ký tự.",
        "PasswordRequiresDigit" => "Mật khẩu phải có ít nhất một chữ số.",
        "PasswordRequiresUpper" => "Mật khẩu phải có ít nhất một chữ hoa.",
        "PasswordRequiresLower" => "Mật khẩu phải có ít nhất một chữ thường.",
        "PasswordRequiresNonAlphanumeric" => "Mật khẩu phải có ít nhất một ký tự đặc biệt.",
        "UserAlreadyInRole" => "Tài khoản này đã có quyền Nhân viên.",
        "UserNotInRole" => "Tài khoản không có quyền Nhân viên.",
        _ => "Không thể lưu tài khoản nhân viên. Vui lòng kiểm tra lại thông tin và thử lại."
    };

    private string CurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private static string NormalizeFullName(string? value) => ProfileInputRules.NormalizeFullName(value);

    private static string NormalizePhoneNumber(string? value) => ProfileInputRules.NormalizePhoneNumber(value);

    private static string MaskCitizenId(string? citizenIdNumber)
    {
        if (string.IsNullOrWhiteSpace(citizenIdNumber))
        {
            return "Chưa cập nhật";
        }

        return citizenIdNumber.Length <= 4
            ? citizenIdNumber
            : new string('•', citizenIdNumber.Length - 4) + citizenIdNumber[^4..];
    }

    private static string LastFour(string citizenIdNumber) =>
        citizenIdNumber.Length <= 4 ? citizenIdNumber : citizenIdNumber[^4..];
}

