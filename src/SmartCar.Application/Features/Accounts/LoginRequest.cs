namespace SmartCar.Application.Features.Accounts;

public sealed record LoginRequest(
    string Email,
    string Password,
    bool RememberMe);
