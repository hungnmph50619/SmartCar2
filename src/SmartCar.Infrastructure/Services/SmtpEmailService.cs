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

    public Task SendPasswordResetEmailAsync(
        string toEmail,
        string resetUrl,
        CancellationToken cancellationToken = default)
    {
        var body = $"""
            <!doctype html>
            <html lang="vi">
            <body style="font-family:Arial,sans-serif;line-height:1.6;color:#1f2937">
                <h2>Khôi phục mật khẩu SmartCar</h2>
                <p>SmartCar đã nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn.</p>
                <p>
                    <a href="{WebUtility.HtmlEncode(resetUrl)}"
                       style="display:inline-block;padding:12px 20px;background:#0d6efd;color:#fff;text-decoration:none;border-radius:6px">
                        Đặt lại mật khẩu
                    </a>
                </p>
                <p>Nếu bạn không yêu cầu thay đổi mật khẩu, hãy bỏ qua email này.</p>
                <p style="font-size:13px;color:#6b7280">Vì lý do bảo mật, không chia sẻ liên kết này cho người khác.</p>
            </body>
            </html>
            """;

        return SendHtmlAsync(
            toEmail,
            "SmartCar - Đặt lại mật khẩu",
            body,
            cancellationToken);
    }

    public Task SendStaffActivationEmailAsync(
        string toEmail,
        string fullName,
        string activationUrl,
        CancellationToken cancellationToken = default)
    {
        var safeName = WebUtility.HtmlEncode(fullName);
        var safeUrl = WebUtility.HtmlEncode(activationUrl);
        var body = $"""
            <!doctype html>
            <html lang="vi">
            <body style="font-family:Arial,sans-serif;line-height:1.6;color:#1f2937">
                <h2>Kích hoạt tài khoản Nhân viên SmartCar</h2>
                <p>Xin chào {safeName},</p>
                <p>Quản trị viên SmartCar đã tạo tài khoản Nhân viên cho bạn.</p>
                <p>Hãy mở liên kết dưới đây để tự thiết lập mật khẩu trước khi đăng nhập lần đầu.</p>
                <p>
                    <a href="{safeUrl}"
                       style="display:inline-block;padding:12px 20px;background:#0d6efd;color:#fff;text-decoration:none;border-radius:6px">
                        Thiết lập mật khẩu
                    </a>
                </p>
                <p>Sau khi đặt mật khẩu, bạn có thể đăng nhập bằng email đã đăng ký.</p>
                <p style="font-size:13px;color:#6b7280">Không chia sẻ liên kết kích hoạt này cho người khác.</p>
            </body>
            </html>
            """;

        return SendHtmlAsync(
            toEmail,
            "SmartCar - Kích hoạt tài khoản Nhân viên",
            body,
            cancellationToken);
    }

    private async Task SendHtmlAsync(
        string toEmail,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfiguration();

        var fromEmail = string.IsNullOrWhiteSpace(_options.FromEmail)
            ? _options.UserName.Trim()
            : _options.FromEmail.Trim();

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, _options.FromName, Encoding.UTF8),
            Subject = subject,
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
                "Chưa cấu hình SMTP cho chức năng gửi email.");
        }
    }
}
