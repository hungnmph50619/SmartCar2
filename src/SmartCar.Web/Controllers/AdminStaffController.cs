using System.Security.Claims;
using System.Security.Cryptography;
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
        var email = model.Email.Trim().ToLowerInvariant();
        var phoneNumber = model.PhoneNumber.Trim();
        var citizenIdNumber = model.CitizenIdNumber.Trim();

        ValidateFullName(fullName, nameof(model.FullName));
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

        var employeeCode = await GenerateEmployeeCodeAsync(cancellationToken);
        var temporaryPassword = GenerateTemporaryPassword();
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

        var createResult = await _userManager.CreateAsync(user, temporaryPassword);
        if (!createResult.Succeeded)
        {
            AddIdentityErrors(createResult);
            return View(model);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, RoleNames.Staff);
        if (!roleResult.Succeeded)
        {
            await _userManager.DeleteAsync(user);
            AddIdentityErrors(roleResult);
            return View(model);
        }

        await WriteAuditAsync(
            "CreateStaffAccount",
            user.Id,
            $"Tạo nhân viên {employeeCode} - {fullName}, gán role Staff và yêu cầu đổi mật khẩu ở lần đăng nhập đầu tiên.",
            cancellationToken);

        // Chỉ hiển thị mật khẩu tạm một lần sau khi tạo, không lưu plaintext vào database/audit log.
        TempData["StaffTemporaryPassword"] = temporaryPassword;
        TempData["SuccessMessage"] =
            $"Đã tạo nhân viên {employeeCode}. Hãy bàn giao email và mật khẩu tạm cho nhân viên; lần đăng nhập đầu tiên sẽ bắt buộc đổi mật khẩu.";

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
            PhoneNumber = user.PhoneNumber ?? string.Empty,
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
        var email = model.Email.Trim().ToLowerInvariant();
        var phoneNumber = model.PhoneNumber.Trim();
        var citizenIdNumber = model.CitizenIdNumber.Trim();

        ValidateFullName(fullName, nameof(model.FullName));
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
        user.VerifiedByUserId = CurrentUserId();
        user.VerifiedAt = DateTime.UtcNow;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            AddIdentityErrors(updateResult);
            return View(model);
        }

        await _userManager.UpdateSecurityStampAsync(user);
        await WriteAuditAsync(
            "UpdateStaffAccount",
            user.Id,
            $"Cập nhật hồ sơ nhân viên {user.EmployeeCode ?? user.Email} - {fullName} và xác minh lại thông tin.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật thông tin nhân viên.";
        return RedirectToAction(nameof(Details), new { id = user.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleStatus(
        string id,
        CancellationToken cancellationToken)
    {
        var user = await GetStaffAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        user.IsActive = !user.IsActive;
        var updateResult = await _userManager.UpdateAsync(user);
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
                : $"Khóa tài khoản nhân viên {user.EmployeeCode ?? user.Email}; giữ nguyên dữ liệu lịch sử.",
            cancellationToken);

        TempData["SuccessMessage"] = user.IsActive
            ? "Đã kích hoạt lại tài khoản nhân viên."
            : "Đã khóa tài khoản nhân viên. Dữ liệu lịch sử vẫn được giữ nguyên.";
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
            CitizenIdNumber = user.CitizenIdNumber,
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
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.Email), "Email này đã được sử dụng.");
        }

        var phoneExists = phoneNumber.Length == 10 && phoneNumber.All(char.IsDigit) &&
            await _userManager.Users.AnyAsync(
                user => user.Id != excludedUserId && user.PhoneNumber == phoneNumber,
                cancellationToken);
        if (phoneExists)
        {
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.PhoneNumber), "Số điện thoại này đã được sử dụng.");
        }

        var citizenExists = citizenIdNumber.Length == 12 && citizenIdNumber.All(char.IsDigit) &&
            await _userManager.Users.AnyAsync(
                user => user.Id != excludedUserId && user.CitizenIdNumber == citizenIdNumber,
                cancellationToken);
        if (citizenExists)
        {
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.CitizenIdNumber), "Số CCCD này đã được gán cho tài khoản nhân viên khác.");
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

    private static string GenerateTemporaryPassword()
    {
        var number = RandomNumberGenerator.GetInt32(100000, 1000000);
        return $"Sc@{number}!";
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

    private static string NormalizeFullName(string value) =>
        string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
