using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Accounts;

public interface IAccountService
{
    Task<OperationResult> RegisterCustomerAsync(
        RegisterCustomerRequest request,
        CancellationToken cancellationToken = default);

    Task<LoginResult> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default);

    Task<string?> GeneratePasswordResetTokenAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default);

    Task LogoutAsync();
}
