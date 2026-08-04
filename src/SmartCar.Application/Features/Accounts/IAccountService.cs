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

    Task LogoutAsync();
}
