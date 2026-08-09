using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class ProfileSettingsController : Controller
{
    private const long MaximumAvatarImageBytes = 2 * 1024 * 1024;
    private const string AvatarUrlPrefix = "/uploads/avatars/";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IWebHostEnvironment _environment;
    private readonly IAuditService _auditService;
    private readonly IUserBankAccountService _bankAccountService;
    private readonly IDocumentService _documentService;

    public ProfileSettingsController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IWebHostEnvironment environment,
        IAuditService auditService,
        IUserBankAccountService bankAccountService,
        IDocumentService documentService)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _environment = environment;
        _auditService = auditService;
        _bankAccountService = bankAccountService;
        _documentService = documentService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadAvatar(
        IFormFile? avatar,
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

        var validationError = await ImageFileValidator.ValidateAsync(
            avatar,
            MaximumAvatarImageBytes,
            cancellationToken);
        if (validationError is not null)
        {
            TempData["ErrorMessage"] = validationError;
            return RedirectToProfile("profile", returnVehicleId, pickupDate, returnDate);
        }

        var webRoot = ResolveWebRoot();
        var avatarDirectory = Path.Combine(webRoot, "uploads", "avatars");
        Directory.CreateDirectory(avatarDirectory);

        var extension = Path.GetExtension(avatar!.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(avatarDirectory, fileName);
        var newAvatarPath = $"{AvatarUrlPrefix}{fileName}";
        var previousAvatarPath = user.AvatarPath;

        try
        {
            await using (var output = new FileStream(
                             fullPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await avatar.CopyToAsync(output, cancellationToken);
            }

            user.AvatarPath = newAvatarPath;
            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                user.AvatarPath = previousAvatarPath;
                DeletePhysicalAvatar(newAvatarPath);
                TempData["ErrorMessage"] = string.Join("; ", result.Errors.Select(error => error.Description));
                return RedirectToProfile("profile", returnVehicleId, pickupDate, returnDate);
            }

            DeletePhysicalAvatar(previousAvatarPath);

            await _auditService.WriteAsync(
                user.Id,
                "UpdateAvatar",
                "UserProfile",
                user.Id,
                "Cập nhật ảnh đại diện tài khoản.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            TempData["SuccessMessage"] = "Đã cập nhật ảnh đại diện.";
            return RedirectToProfile("profile", returnVehicleId, pickupDate, returnDate);
        }
        catch
        {
            DeletePhysicalAvatar(newAvatarPath);
            throw;
        }
    }

    [Authorize(Roles = RoleNames.Customer)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBankAccount(
        BankAccountViewModel model,
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

        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .ToList();
            TempData["ErrorMessage"] = errors.Count > 0
                ? string.Join("; ", errors)
                : "Vui lòng kiểm tra lại thông tin tài khoản ngân hàng.";
            return RedirectToProfile("banking", returnVehicleId, pickupDate, returnDate);
        }

        try
        {
            await _bankAccountService.SaveDefaultAsync(
                user.Id,
                model.BankCode,
                model.AccountNumber,
                model.AccountHolderName,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToProfile("banking", returnVehicleId, pickupDate, returnDate);
        }

        var documents = await _documentService.GetCustomerDocumentsAsync(user.Id, cancellationToken);
        var citizenName = documents
            .FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId)
            ?.FullNameOnDocument;
        var referenceName = string.IsNullOrWhiteSpace(citizenName) ? user.FullName : citizenName;
        var holderMatches = string.Equals(
            NormalizePersonName(referenceName ?? string.Empty),
            NormalizePersonName(model.AccountHolderName),
            StringComparison.Ordinal);

        await _auditService.WriteAsync(
            user.Id,
            "UpdateBankAccount",
            "UserBankAccount",
            user.Id,
            $"Cập nhật tài khoản nhận hoàn tiền tại ngân hàng {model.BankCode}; số tài khoản kết thúc bằng {LastFour(model.AccountNumber)}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã lưu tài khoản ngân hàng mặc định để nhận các khoản hoàn tiền từ SmartCar.";
        if (!holderMatches)
        {
            TempData["WarningMessage"] = "Tên chủ tài khoản ngân hàng chưa khớp với tên trên hồ sơ CCCD. Hệ thống vẫn lưu nhưng quản trị viên có thể yêu cầu bạn kiểm tra lại trước khi hoàn tiền.";
        }

        return RedirectToProfile("banking", returnVehicleId, pickupDate, returnDate);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordViewModel model,
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

        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .ToList();

            TempData["ErrorMessage"] = errors.Count > 0
                ? string.Join("; ", errors)
                : "Vui lòng kiểm tra lại thông tin đổi mật khẩu.";
            return RedirectToProfile("security", returnVehicleId, pickupDate, returnDate);
        }

        if (string.Equals(model.CurrentPassword, model.NewPassword, StringComparison.Ordinal))
        {
            TempData["ErrorMessage"] = "Mật khẩu mới phải khác mật khẩu hiện tại.";
            return RedirectToProfile("security", returnVehicleId, pickupDate, returnDate);
        }

        var result = await _userManager.ChangePasswordAsync(
            user,
            model.CurrentPassword,
            model.NewPassword);

        if (!result.Succeeded)
        {
            var errors = result.Errors
                .Select(error => error.Code == "PasswordMismatch"
                    ? "Mật khẩu hiện tại không chính xác."
                    : error.Description)
                .Distinct();

            TempData["ErrorMessage"] = string.Join("; ", errors);
            return RedirectToProfile("security", returnVehicleId, pickupDate, returnDate);
        }

        await _signInManager.RefreshSignInAsync(user);
        await _auditService.WriteAsync(
            user.Id,
            "ChangePassword",
            "UserAccount",
            user.Id,
            "Người dùng chủ động đổi mật khẩu trong trang hồ sơ.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đổi mật khẩu thành công.";
        return RedirectToProfile("security", returnVehicleId, pickupDate, returnDate);
    }

    private IActionResult RedirectToProfile(
        string tab,
        int? returnVehicleId,
        DateTime? pickupDate,
        DateTime? returnDate) =>
        RedirectToAction("Index", "Profile", new
        {
            tab,
            returnVehicleId,
            pickupDate,
            returnDate
        });

    private string ResolveWebRoot() =>
        string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;

    private void DeletePhysicalAvatar(string? avatarPath)
    {
        if (string.IsNullOrWhiteSpace(avatarPath) ||
            !avatarPath.StartsWith(AvatarUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = Path.GetFileName(avatarPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var avatarDirectory = Path.Combine(ResolveWebRoot(), "uploads", "avatars");
        var fullPath = Path.Combine(avatarDirectory, fileName);
        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }
    }

    private static string NormalizePersonName(string value)
    {
        var vietnameseNormalized = value
            .Replace('Đ', 'D')
            .Replace('đ', 'd');
        var decomposed = vietnameseNormalized.Trim().Normalize(NormalizationForm.FormD);
        var characters = decomposed
            .Where(character =>
                CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark &&
                char.IsLetterOrDigit(character))
            .Select(char.ToUpperInvariant);
        return new string(characters.ToArray());
    }

    private static string LastFour(string accountNumber)
    {
        var digits = new string(accountNumber.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? digits : digits[^4..];
    }
}
