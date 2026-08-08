using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace SmartCar.Web.Services;

public sealed class KycTestingOptions
{
    public bool Enabled { get; set; }
}

public sealed class KycTestingService
{
    public const string CookieName = "SmartCarKycTest";

    private readonly IWebHostEnvironment _environment;
    private readonly KycTestingOptions _options;

    public KycTestingService(
        IWebHostEnvironment environment,
        IOptions<KycTestingOptions> options)
    {
        _environment = environment;
        _options = options.Value;
    }

    public bool Available => _environment.IsDevelopment() && _options.Enabled;

    public bool IsActive(HttpContext? context) =>
        Available &&
        context is not null &&
        string.Equals(context.Request.Cookies[CookieName], "1", StringComparison.Ordinal);
}

public static class KycTestSamples
{
    public const string CitizenDocumentNumber = "099999999999";
    public const string CitizenFullName = "NGUYỄN VĂN TEST";
    public static readonly DateTime CitizenDateOfBirth = new(1998, 5, 15);
    public const string CitizenGender = "Nam";
    public static readonly DateTime CitizenIssuedDate = new(2025, 1, 1);
    public static readonly DateTime CitizenExpiryDate = new(2040, 5, 15);
    public const string CitizenAddress = "123 Đường Test, TP. Ninh Bình, Ninh Bình";

    public const string LicenseDocumentNumber = "TESTB123456";
    public const string LicenseClass = "B";
    public static readonly DateTime LicenseIssuedDate = new(2025, 1, 2);
    public static readonly DateTime LicenseExpiryDate = new(2035, 5, 15);

    public static EkycOcrResult CreateCitizenOcrResult()
    {
        var sessionId = $"smartcar-test-{Guid.NewGuid():N}";
        return new EkycOcrResult(
            Succeeded: true,
            IsDemo: true,
            Provider: "SmartCar KYC Test Mode",
            SessionId: sessionId,
            DocumentNumber: CitizenDocumentNumber,
            FullName: CitizenFullName,
            DateOfBirth: CitizenDateOfBirth,
            Gender: CitizenGender,
            IssuedDate: CitizenIssuedDate,
            ExpiryDate: CitizenExpiryDate,
            Address: CitizenAddress,
            LicenseClass: null,
            OcrConfidence: 100m,
            Message: "Chế độ kiểm thử Development: dữ liệu CCCD mẫu được mô phỏng để kiểm tra luồng hệ thống. Không phải kết quả xác thực giấy tờ thật.",
            CheckedAtUtc: DateTime.UtcNow);
    }
}
