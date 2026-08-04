namespace SmartCar.Application.Features.Accounts;

public sealed record RegisterCustomerRequest(
    string FullName,
    string PhoneNumber,
    string Email,
    string Password);
