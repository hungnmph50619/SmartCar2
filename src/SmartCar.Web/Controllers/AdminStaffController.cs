using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Accounts;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminStaffController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly IEmailService _emailService;
    private readonly ILogger<AdminStaffController> _logger;

    public AdminStaffController(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext dbContext,
        IAuditService auditService,
        IEmailService emailService,
        ILogger<AdminStaffController> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _auditService = auditService;
        _emailService = emailService;
        _logger = logger;
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
        if (!model.ConfirmInformation)
        {
            ModelState.AddModelError(
                nameof(model.ConfirmInformation),
                "Quản trị viên phải xác nhận đã đối chiếu hồ sơ nhân viên trước khi tạo tài khoản.");
        }

        var employeeCode = NormalizeEmployeeCode(model.EmployeeCode);
        var fullName = NormalizeFullName(model.FullName);
        var email = model.Email.Trim().ToLowerInvariant();
        var phoneNumber = NormalizePhoneNumber(model.PhoneNumber);
        var citizenIdNumber = NormalizeCitizenId(model.CitizenIdNumber);

        await ValidateStaffInputAsync(
            employeeCode,
            email,
            phoneNumber,
            citizenIdNumber,
            excludedUserId: null,
            cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

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
            VerifiedAt = now
        };

        // Không cấp mật khẩu mặc định. Admin chỉ tạo tài khoản + role Staff;
        // nhân viên tự thiết lập mật khẩu bằng liên kết kích hoạt.
        var createResult = await _userManager.CreateAsync(user);
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
            $"Tạo nhân viên {employeeCode} - {fullName}, gán role Staff và kích hoạt tài khoản.",
            cancellationToken);

        var activationUrl = await BuildActivationUrlAsync(user, cancellationToken);
        var emailSent = activationUrl is not null &&
                        await TrySendActivationEmailAsync(user, activationUrl, cancellationToken);

        if (!string.IsNullOrWhiteSpace(activationUrl))
        {
            TempData["StaffActivationUrl"] = activationUrl;
        }

        TempData["SuccessMessage"] = emailSent
            ? "Đã tạo tài khoản nhân viên và gửi email kích hoạt để nhân viên tự đặt mật khẩu."
            : "Đã tạo tài khoản nhân viên. Chưa gửi được email kích hoạt; Quản trị viên có thể sao chép liên kết kích hoạt ở trang chi tiết.";

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

        var employeeCode = NormalizeEmployeeCode(model.EmployeeCode);
        var fullName = NormalizeFullName(model.FullName);
        var email = model.Email.Trim().ToLowerInvariant();
        var phoneNumber = NormalizePhoneNumber(model.PhoneNumber);
        var citizenIdNumber = NormalizeCitizenId(model.CitizenIdNumber);

        await ValidateStaffInputAsync(
            employeeCode,
            email,
            phoneNumber,
            citizenIdNumber,
            user.Id,
            cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        user.EmployeeCode = employeeCode;
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
            $"Cập nhật hồ sơ nhân viên {employeeCode} - {fullName} và xác minh lại thông tin.",
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
            TempData["ErrorMessage"] = string.Join("; ", updateResult.Errors.Select(error => error.Description));
            return RedirectToAction(nameof(Details), new { id });
        }

        // Đổi security stamp để phiên đăng nhập cũ của nhân viên bị vô hiệu hóa sau khi khóa.
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendActivation(
        string id,
        CancellationToken cancellationToken)
    {
        var user = await GetStaffAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (!user.IsActive)
        {
            TempData["ErrorMessage"] = "Tài khoản đang bị khóa. Hãy kích hoạt lại trước khi gửi liên kết thiết lập mật khẩu.";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (await _userManager.HasPasswordAsync(user))
        {
            TempData["ErrorMessage"] = "Nhân viên đã thiết lập mật khẩu. Có thể dùng chức năng Quên mật khẩu nếu cần đặt lại.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var activationUrl = await BuildActivationUrlAsync(user, cancellationToken);
        if (string.IsNullOrWhiteSpace(activationUrl))
        {
            TempData["ErrorMessage"] = "Không thể tạo liên kết kích hoạt tài khoản.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var emailSent = await TrySendActivationEmailAsync(user, activationUrl, cancellationToken);
        TempData["StaffActivationUrl"] = activationUrl;
        TempData[emailSent ? "SuccessMessage" : "ErrorMessage"] = emailSent
            ? "Đã gửi lại email kích hoạt cho nhân viên."
            : "Không gửi được email kích hoạt. Bạn vẫn có thể sao chép liên kết kích hoạt bên dưới và gửi cho nhân viên.";

        await WriteAuditAsync(
            "ResendStaffActivation",
            user.Id,
            $"Tạo lại liên kết thiết lập mật khẩu cho nhân viên {user.EmployeeCode ?? user.Email}.",
            cancellationToken);

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
        string employeeCode,
        string email,
        string phoneNumber,
        string citizenIdNumber,
        string? excludedUserId,
        CancellationToken cancellationToken)
    {
        if (employeeCode.Length is < 2 or > 30 ||
            employeeCode.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            ModelState.AddModelError(
                nameof(AdminStaffCreateViewModel.EmployeeCode),
                "Mã nhân viên chỉ gồm chữ, số, dấu gạch ngang hoặc gạch dưới và dài 2-30 ký tự.");
        }

        if (!IsValidNormalizedPhoneNumber(phoneNumber))
        {
            ModelState.AddModelError(
                nameof(AdminStaffCreateViewModel.PhoneNumber),
                "Số điện thoại phải gồm 10 chữ số và bắt đầu bằng 0, hoặc dùng mã quốc gia +84.");
        }

        if (citizenIdNumber.Length != 12 || !citizenIdNumber.All(char.IsDigit))
        {
            ModelState.AddModelError(
                nameof(AdminStaffCreateViewModel.CitizenIdNumber),
                "CCCD nhân viên phải gồm đúng 12 chữ số.");
        }

        var existingEmail = await _userManager.FindByEmailAsync(email);
        if (existingEmail is not null && existingEmail.Id != excludedUserId)
        {
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.Email), "Email này đã được sử dụng.");
        }

        var phoneExists = IsValidNormalizedPhoneNumber(phoneNumber) &&
            await _userManager.Users.AnyAsync(
                user =>
                    user.Id != excludedUserId &&
                    user.PhoneNumber == phoneNumber,
                cancellationToken);
        if (phoneExists)
        {
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.PhoneNumber), "Số điện thoại này đã được sử dụng.");
        }

        var codeExists = !string.IsNullOrWhiteSpace(employeeCode) &&
            await _userManager.Users.AnyAsync(
                user => user.Id != excludedUserId && user.EmployeeCode == employeeCode,
                cancellationToken);
        if (codeExists)
        {
            ModelState.AddModelError(nameof(AdminStaffCreateViewModel.EmployeeCode), "Mã nhân viên này đã tồn tại.");
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

    private async Task<string?> BuildActivationUrlAsync(
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        return Url.Action(
            "ResetPassword",
            "Account",
            new { email = user.Email, token },
            Request.Scheme);
    }

    private async Task<bool> TrySendActivationEmailAsync(
        ApplicationUser user,
        string activationUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            return false;
        }

        try
        {
            await _emailService.SendStaffActivationEmailAsync(
                user.Email,
                user.FullName,
                activationUrl,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Không thể gửi email kích hoạt tài khoản Staff {StaffId} tới {Email}.",
                user.Id,
                user.Email);
            return false;
        }
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
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }

    private string CurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private static string NormalizeEmployeeCode(string value) =>
        value.Trim().ToUpperInvariant();

    private static string NormalizeFullName(string value) =>
        string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeCitizenId(string value) =>
        value.Trim()
            .Replace(" ", string.Empty)
            .Replace(".", string.Empty)
            .Replace("-", string.Empty);

    private static string NormalizePhoneNumber(string value)
    {
        var phone = value.Trim()
            .Replace(" ", string.Empty)
            .Replace(".", string.Empty)
            .Replace("-", string.Empty);

        if (phone.StartsWith("+84", StringComparison.Ordinal))
        {
            phone = $"0{phone[3..]}";
        }

        return phone;
    }

    private static bool IsValidNormalizedPhoneNumber(string value) =>
        value.Length == 10 && value[0] == '0' && value.All(char.IsDigit);
}
