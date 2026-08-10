using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;
using SmartCar.Application.Features.Accounts;

namespace SmartCar.Infrastructure.Services;

internal sealed class SmtpEmailService : IEmailService
{
    private readonly SmtpEmailOptions _options;

    public SmtpEmailService(IOptions<SmtpEmailOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendPasswordResetEmailAsync(
        string toEmail,
        string resetUrl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfiguration();

        var fromEmail = string.IsNullOrWhiteSpace(_options.FromEmail)
            ? _options.UserName.Trim()
            : _options.FromEmail.Trim();

        var encodedResetUrl = WebUtility.HtmlEncode(resetUrl);
        var body = $"""
            <!doctype html>
            <html lang="vi">
            <body style="font-family:Arial,sans-serif;line-height:1.6;color:#1f2937">
                <h2>Khôi phục mật khẩu SmartCar</h2>
                <p>SmartCar đã nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn.</p>
                <p>
                    <a href="{encodedResetUrl}"
                       style="display:inline-block;padding:12px 20px;background:#0d6efd;color:#fff;text-decoration:none;border-radius:6px">
                        Đặt lại mật khẩu
                    </a>
                </p>
                <p>Liên kết có hiệu lực trong 30 phút. Nếu bạn không yêu cầu thay đổi mật khẩu, hãy bỏ qua email này.</p>
                <p style="font-size:13px;color:#6b7280">Vì lý do bảo mật, không chia sẻ liên kết này cho người khác.</p>
            </body>
            </html>
            """;

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, _options.FromName, Encoding.UTF8),
            Subject = "SmartCar - Đặt lại mật khẩu",
            SubjectEncoding = Encoding.UTF8,
            Body = body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true
        };
        message.To.Add(new MailAddress(toEmail.Trim()));

        using var smtpClient = new SmtpClient(_options.Host.Trim(), _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(
                _options.UserName.Trim(),
                _options.Password)
        };

        await smtpClient.SendMailAsync(message, cancellationToken);
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.Host) ||
            _options.Port <= 0 ||
            string.IsNullOrWhiteSpace(_options.UserName) ||
            string.IsNullOrWhiteSpace(_options.Password))
        {
            throw new InvalidOperationException(
                "Chưa cấu hình SMTP cho chức năng gửi email khôi phục mật khẩu.");
        }
    }
}
