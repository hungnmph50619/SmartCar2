using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Accounts;
using SmartCar.Domain.Constants;

namespace SmartCar.Infrastructure.Identity;

internal sealed class AccountService : IAccountService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AccountService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    public async Task<OperationResult> RegisterCustomerAsync(
        RegisterCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();
        var phoneNumber = request.PhoneNumber.Trim();

        if (await _userManager.FindByEmailAsync(email) is not null)
        {
            return OperationResult.Failure("Email này đã được sử dụng.");
        }

        var phoneExists = await _userManager.Users
            .AnyAsync(user => user.PhoneNumber == phoneNumber, cancellationToken);

        if (phoneExists)
        {
            return OperationResult.Failure("Số điện thoại này đã được sử dụng.");
        }

        var user = new ApplicationUser
        {
            FullName = request.FullName.Trim(),
            UserName = email,
            Email = email,
            PhoneNumber = phoneNumber,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            return OperationResult.Failure(createResult.Errors.Select(error => error.Description));
        }

        var roleResult = await _userManager.AddToRoleAsync(user, RoleNames.Customer);
        if (!roleResult.Succeeded)
        {
            await _userManager.DeleteAsync(user);
            return OperationResult.Failure(roleResult.Errors.Select(error => error.Description));
        }

        return OperationResult.Success();
    }

    public async Task<LoginResult> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            return LoginResult.Failure("Email, mật khẩu không đúng hoặc tài khoản đã bị khóa.");
        }

        var signInResult = await _signInManager.PasswordSignInAsync(
            user,
            request.Password,
            request.RememberMe,
            lockoutOnFailure: true);

        if (signInResult.IsLockedOut)
        {
            return LoginResult.Failure(
                "Bạn đã nhập sai quá nhiều lần. Tài khoản tạm khóa trong 10 phút.");
        }

        if (!signInResult.Succeeded)
        {
            return LoginResult.Failure("Email hoặc mật khẩu không đúng.");
        }

        var isAdmin = await _userManager.IsInRoleAsync(user, RoleNames.Admin);
        return LoginResult.Success(isAdmin);
    }

    public async Task<string?> GeneratePasswordResetTokenAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await _userManager.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive)
        {
            return null;
        }

        return await _userManager.GeneratePasswordResetTokenAsync(user);
    }

    public async Task<OperationResult> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await _userManager.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive)
        {
            return OperationResult.Failure("Liên kết đặt lại mật khẩu không hợp lệ hoặc đã hết hạn.");
        }

        var resetResult = await _userManager.ResetPasswordAsync(user, token, newPassword);
        if (!resetResult.Succeeded)
        {
            return OperationResult.Failure(resetResult.Errors.Select(error => error.Description));
        }

        await _userManager.ResetAccessFailedCountAsync(user);
        return OperationResult.Success();
    }

    public Task LogoutAsync() => _signInManager.SignOutAsync();
}
