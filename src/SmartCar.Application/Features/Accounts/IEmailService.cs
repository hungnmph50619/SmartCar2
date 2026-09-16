namespace SmartCar.Application.Features.Accounts;

public interface IEmailService
{
    Task SendPasswordResetEmailAsync(
        string toEmail,
        string resetUrl,
        CancellationToken cancellationToken = default);

    Task SendStaffActivationEmailAsync(
        string toEmail,
        string fullName,
        string activationUrl,
        CancellationToken cancellationToken = default);
}
