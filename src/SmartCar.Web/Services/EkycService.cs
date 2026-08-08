using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace SmartCar.Web.Services;

public sealed class EkycService : IEkycService
{
    private readonly HttpClient _httpClient;
    private readonly EkycOptions _options;
    private readonly ILogger<EkycService> _logger;

    public EkycService(
        HttpClient httpClient,
        IOptions<EkycOptions> options,
        ILogger<EkycService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsDemoMode =>
        string.Equals(_options.Mode, "Demo", StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(_options.Mode, "Auto", StringComparison.OrdinalIgnoreCase) &&
         string.IsNullOrWhiteSpace(_options.ApiKey));

    public string ProviderLabel => IsDemoMode ? "Demo eKYC" : _options.Provider;

    public Task<EkycOcrResult> ReadCitizenIdAsync(
        IFormFile frontImage,
        IFormFile backImage,
        CancellationToken cancellationToken = default) =>
        ReadCitizenIdInternalAsync(frontImage, backImage, cancellationToken);

    public Task<EkycOcrResult> ReadDrivingLicenseAsync(
        IFormFile frontImage,
        IFormFile backImage,
        CancellationToken cancellationToken = default) =>
        ReadDrivingLicenseInternalAsync(frontImage, backImage, cancellationToken);

    public async Task<EkycFaceVerificationResult> VerifyFaceAsync(
        IFormFile citizenFrontImage,
        IFormFile selfieVideo,
        CancellationToken cancellationToken = default)
    {
        if (citizenFrontImage.Length <= 0)
        {
            return FaceFailure("Ảnh CCCD mặt trước trống. Vui lòng chọn lại ảnh.");
        }

        if (selfieVideo.Length <= 0)
        {
            return FaceFailure("Video khuôn mặt trống. Vui lòng quay lại.");
        }

        if (IsDemoMode)
        {
            return new EkycFaceVerificationResult(
                true,
                true,
                ProviderLabel,
                true,
                true,
                95m,
                "Chế độ demo: luồng quay khuôn mặt đã hoàn tất. Kết quả liveness/face match là mô phỏng và vẫn cần Quản trị viên duyệt.",
                DateTime.UtcNow);
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return FaceFailure("Chưa cấu hình API key của nhà cung cấp eKYC.");
        }

        try
        {
            using var form = new MultipartFormDataContent();
            AddFile(form, selfieVideo, "video");
            AddFile(form, citizenFrontImage, "cmnd");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.LivenessUrl)
            {
                Content = form
            };
            request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "FPT Reader liveness failed with HTTP {StatusCode}: {Payload}",
                    response.StatusCode,
                    TrimForLog(payload));
                return FaceFailure("Nhà cung cấp eKYC chưa thể xác minh khuôn mặt. Vui lòng thử lại.");
            }

            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            var code = GetScalar(root, "code");
            var message = GetScalar(root, "message") ?? "Không nhận được kết quả xác minh khuôn mặt.";

            bool? isLive;
            if (root.TryGetProperty("liveness", out var liveness) && liveness.ValueKind == JsonValueKind.Object)
            {
                isLive = ParseBoolean(GetScalar(liveness, "is_live"));
            }
            else
            {
                isLive = ParseBoolean(GetScalar(root, "is_live"));
            }

            bool? faceMatched = null;
            decimal? similarity = null;
            if (root.TryGetProperty("face_match", out var faceMatch) && faceMatch.ValueKind == JsonValueKind.Object)
            {
                faceMatched = ParseBoolean(GetScalar(faceMatch, "isMatch"));
                similarity = ParseDecimal(GetScalar(faceMatch, "similarity"));
            }

            var threshold = _options.FaceMatchThreshold <= 0 ? 80m : _options.FaceMatchThreshold;
            var succeeded = code == "200" &&
                            isLive == true &&
                            faceMatched == true &&
                            similarity.HasValue &&
                            similarity.Value >= threshold;

            return new EkycFaceVerificationResult(
                succeeded,
                false,
                ProviderLabel,
                isLive,
                faceMatched,
                similarity,
                succeeded
                    ? "Đã kiểm tra người thật và đối chiếu khuôn mặt với ảnh trên CCCD."
                    : message,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể gọi FPT Reader liveness/face match.");
            return FaceFailure("Không thể kết nối dịch vụ eKYC. Vui lòng thử lại hoặc dùng xác minh thủ công.");
        }
    }

    private async Task<EkycOcrResult> ReadCitizenIdInternalAsync(
        IFormFile frontImage,
        IFormFile backImage,
        CancellationToken cancellationToken)
    {
        if (IsDemoMode)
        {
            return DemoOcrResult();
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return OcrFailure("Chưa cấu hình API key của nhà cung cấp eKYC.");
        }

        try
        {
            var front = await ReadImageAsync(
                frontImage,
                _options.CitizenIdOcrUrl,
                cancellationToken);
            if (!front.Succeeded)
            {
                return OcrFailure($"CCCD mặt trước: {front.Message}");
            }

            var back = await ReadImageAsync(
                backImage,
                _options.CitizenIdOcrUrl,
                cancellationToken);
            if (!back.Succeeded)
            {
                return OcrFailure($"CCCD mặt sau: {back.Message}");
            }

            var confidence = AverageProbability(front, back);
            return new EkycOcrResult(
                true,
                false,
                ProviderLabel,
                $"fpt-{Guid.NewGuid():N}",
                Find(front, back, "id"),
                Find(front, back, "name"),
                ParseDate(Find(front, back, "dob")),
                NormalizeGender(Find(front, back, "sex")),
                ParseDate(Find(back, front, "issue_date", "date")),
                ParseDate(Find(front, back, "doe")),
                Find(front, back, "address"),
                null,
                confidence,
                "Đã đọc CCCD bằng FPT.AI Reader. Vui lòng kiểm tra lại dữ liệu trước khi quay khuôn mặt.",
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể gọi FPT Reader OCR cho CCCD.");
            return OcrFailure("Không thể kết nối dịch vụ OCR CCCD. Vui lòng thử lại hoặc dùng xác minh thủ công.");
        }
    }

    private async Task<EkycOcrResult> ReadDrivingLicenseInternalAsync(
        IFormFile frontImage,
        IFormFile backImage,
        CancellationToken cancellationToken)
    {
        if (IsDemoMode)
        {
            return DemoOcrResult();
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return OcrFailure("Chưa cấu hình API key của nhà cung cấp eKYC.");
        }

        try
        {
            var front = await ReadImageAsync(
                frontImage,
                _options.DrivingLicenseOcrUrl,
                cancellationToken);
            if (!front.Succeeded)
            {
                return OcrFailure($"GPLX mặt trước: {front.Message}");
            }

            var back = await ReadImageAsync(
                backImage,
                _options.DrivingLicenseOcrUrl,
                cancellationToken);
            if (!back.Succeeded)
            {
                return OcrFailure($"GPLX mặt sau: {back.Message}");
            }

            var confidence = AverageProbability(front, back);
            return new EkycOcrResult(
                true,
                false,
                ProviderLabel,
                $"fpt-dlr-{Guid.NewGuid():N}",
                Find(front, back, "id"),
                Find(front, back, "name"),
                ParseDate(Find(front, back, "dob")),
                null,
                ParseDate(Find(front, back, "date", "issue_date")),
                ParseDate(Find(front, back, "doe")),
                Find(front, back, "address"),
                NormalizeLicenseClass(Find(front, back, "class")),
                confidence,
                "Đã đọc GPLX bằng FPT.AI Reader. Vui lòng kiểm tra lại dữ liệu trước khi gửi.",
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể gọi FPT Reader OCR cho GPLX.");
            return OcrFailure("Không thể kết nối dịch vụ OCR GPLX. Vui lòng thử lại hoặc nhập thủ công.");
        }
    }

    private async Task<ParsedOcrImage> ReadImageAsync(
        IFormFile image,
        string endpoint,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        AddFile(form, image, "image");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = form
        };
        request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "FPT Reader OCR failed with HTTP {StatusCode}: {Payload}",
                response.StatusCode,
                TrimForLog(payload));
            return ParsedOcrImage.Failure("Dịch vụ OCR trả về lỗi kết nối.");
        }

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        var errorCode = GetScalar(root, "errorCode");
        if (!string.Equals(errorCode, "0", StringComparison.OrdinalIgnoreCase))
        {
            return ParsedOcrImage.Failure(
                GetScalar(root, "errorMessage") ?? "Không đọc được giấy tờ từ ảnh đã chọn.");
        }

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0 ||
            data[0].ValueKind != JsonValueKind.Object)
        {
            return ParsedOcrImage.Failure("OCR không trả về dữ liệu giấy tờ.");
        }

        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var probabilities = new List<decimal>();

        foreach (var property in data[0].EnumerateObject())
        {
            if (property.Name.EndsWith("_prob", StringComparison.OrdinalIgnoreCase))
            {
                var probability = ParseDecimal(ElementToString(property.Value));
                if (probability.HasValue)
                {
                    probabilities.Add(probability.Value);
                }
                continue;
            }

            var value = ElementToString(property.Value);
            if (!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
            {
                fields[property.Name] = value.Trim();
            }
        }

        return new ParsedOcrImage(true, string.Empty, fields, probabilities);
    }

    private static string? Find(
        ParsedOcrImage primary,
        ParsedOcrImage secondary,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (primary.Fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (secondary.Fields.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static decimal? AverageProbability(params ParsedOcrImage[] images)
    {
        var values = images
            .SelectMany(image => image.Probabilities)
            .Where(value => value >= 0)
            .ToArray();

        return values.Length == 0
            ? null
            : Math.Round(values.Average(), 2);
    }

    private static void AddFile(MultipartFormDataContent form, IFormFile file, string fieldName)
    {
        var content = new StreamContent(file.OpenReadStream());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType);
        form.Add(content, fieldName, Path.GetFileName(file.FileName));
    }

    private EkycOcrResult DemoOcrResult() =>
        new(
            true,
            true,
            ProviderLabel,
            $"demo-{Guid.NewGuid():N}",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "Chế độ demo: đã tạo phiên eKYC nhưng không đọc dữ liệu thật từ ảnh. Hãy nhập thông tin thủ công; kết quả khuôn mặt sau đó cũng chỉ là mô phỏng.",
            DateTime.UtcNow);

    private EkycOcrResult OcrFailure(string message) =>
        new(
            false,
            IsDemoMode,
            ProviderLabel,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            message,
            DateTime.UtcNow);

    private EkycFaceVerificationResult FaceFailure(string message) =>
        new(
            false,
            IsDemoMode,
            ProviderLabel,
            null,
            null,
            null,
            message,
            DateTime.UtcNow);

    private static string? GetScalar(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return ElementToString(property);
    }

    private static string? ElementToString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => element.GetArrayLength() == 0
                ? null
                : ElementToString(element[0]),
            _ => null
        };

    private static bool? ParseBoolean(string? value)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value?.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return decimal.TryParse(
            value,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var formats = new[]
        {
            "dd/MM/yyyy",
            "d/M/yyyy",
            "dd-MM-yyyy",
            "d-M-yyyy",
            "yyyy-MM-dd"
        };

        return DateTime.TryParseExact(
            value.Trim(),
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.Date
            : null;
    }

    private static string? NormalizeGender(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            "NAM" or "MALE" or "M" => "Nam",
            "NỮ" or "NU" or "FEMALE" or "F" => "Nữ",
            _ => "Khác"
        };
    }

    private static string? NormalizeLicenseClass(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var firstClass = value
            .Split(new[] { ',', ';', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return firstClass?.Trim().ToUpperInvariant();
    }

    private static string TrimForLog(string value) =>
        value.Length <= 800 ? value : value[..800];

    private sealed record ParsedOcrImage(
        bool Succeeded,
        string Message,
        IReadOnlyDictionary<string, string?> Fields,
        IReadOnlyList<decimal> Probabilities)
    {
        public static ParsedOcrImage Failure(string message) =>
            new(
                false,
                message,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<decimal>());
    }
}
