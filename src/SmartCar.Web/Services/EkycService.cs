using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        ReadDocumentAsync(frontImage, backImage, "idr", cancellationToken);

    public Task<EkycOcrResult> ReadDrivingLicenseAsync(
        IFormFile frontImage,
        IFormFile backImage,
        CancellationToken cancellationToken = default) =>
        ReadDocumentAsync(frontImage, backImage, "dlr", cancellationToken);

    public async Task<EkycFaceVerificationResult> VerifyFaceAsync(
        string sessionId,
        IFormFile selfieVideo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return FaceFailure("Không tìm thấy phiên eKYC. Vui lòng đọc lại CCCD.");
        }

        if (selfieVideo.Length <= 0)
        {
            return FaceFailure("Video khuôn mặt trống. Vui lòng quay lại.");
        }

        if (IsDemoMode)
        {
            if (!sessionId.StartsWith("demo-", StringComparison.OrdinalIgnoreCase))
            {
                return FaceFailure("Phiên eKYC demo không hợp lệ.");
            }

            return new EkycFaceVerificationResult(
                true,
                true,
                ProviderLabel,
                true,
                true,
                95m,
                "Chế độ demo: luồng quay khuôn mặt đã hoàn tất. Kết quả này chỉ dùng để trình diễn và vẫn cần Quản trị viên duyệt.",
                DateTime.UtcNow);
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return FaceFailure("Chưa cấu hình API key của nhà cung cấp eKYC.");
        }

        try
        {
            using var content = new MultipartFormDataContent();
            var videoContent = new StreamContent(selfieVideo.OpenReadStream());
            videoContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
                string.IsNullOrWhiteSpace(selfieVideo.ContentType)
                    ? "application/octet-stream"
                    : selfieVideo.ContentType);
            content.Add(videoContent, "video", Path.GetFileName(selfieVideo.FileName));

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildUrl("face/liveness"))
            {
                Content = content
            };
            AddProviderHeaders(request, sessionId);
            request.Headers.TryAddWithoutValidation("auto", "False");
            request.Headers.TryAddWithoutValidation("lang", "vi");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "FPT eKYC face verification failed with HTTP {StatusCode}: {Payload}",
                    response.StatusCode,
                    TrimForLog(payload));
                return FaceFailure("Nhà cung cấp eKYC chưa thể xác minh khuôn mặt. Vui lòng thử lại.");
            }

            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            var code = GetString(root, "code");
            var message = GetString(root, "message") ?? "Không nhận được kết quả xác minh khuôn mặt.";

            bool? isLive = null;
            if (root.TryGetProperty("liveness", out var liveness))
            {
                isLive = ParseBoolean(GetString(liveness, "is_live"));
            }

            bool? faceMatched = null;
            decimal? similarity = null;
            if (root.TryGetProperty("face_match", out var faceMatch))
            {
                faceMatched = ParseBoolean(GetString(faceMatch, "isMatch"));
                similarity = ParseDecimal(GetString(faceMatch, "similarity"));
            }

            var threshold = _options.FaceMatchThreshold <= 0 ? 80m : _options.FaceMatchThreshold;
            var succeeded = code == "200" &&
                            isLive == true &&
                            faceMatched == true &&
                            (!similarity.HasValue || similarity.Value >= threshold);

            return new EkycFaceVerificationResult(
                succeeded,
                false,
                ProviderLabel,
                isLive,
                faceMatched,
                similarity,
                succeeded
                    ? "Đã kiểm tra người thật và đối chiếu khuôn mặt với CCCD."
                    : message,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể gọi FPT eKYC để kiểm tra khuôn mặt.");
            return FaceFailure("Không thể kết nối dịch vụ eKYC. Vui lòng thử lại hoặc dùng xác minh thủ công.");
        }
    }

    private async Task<EkycOcrResult> ReadDocumentAsync(
        IFormFile frontImage,
        IFormFile backImage,
        string documentType,
        CancellationToken cancellationToken)
    {
        if (IsDemoMode)
        {
            return new EkycOcrResult(
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
                "Chế độ demo: đã tạo phiên eKYC nhưng không đọc dữ liệu thật từ ảnh. Hãy nhập thông tin thủ công; video khuôn mặt sẽ chỉ được mô phỏng kết quả.",
                DateTime.UtcNow);
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return OcrFailure("Chưa cấu hình API key của nhà cung cấp eKYC.");
        }

        try
        {
            var sessionId = await InitializeSessionAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return OcrFailure("Không thể khởi tạo phiên eKYC.");
            }

            using var form = new MultipartFormDataContent();
            AddImage(form, frontImage, "files");
            AddImage(form, backImage, "files");

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("ocr"))
            {
                Content = form
            };
            AddProviderHeaders(request, sessionId);
            request.Headers.TryAddWithoutValidation("document-type", documentType);
            request.Headers.TryAddWithoutValidation("lang", "vi");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "FPT eKYC OCR failed with HTTP {StatusCode}: {Payload}",
                    response.StatusCode,
                    TrimForLog(payload));
                return OcrFailure("Nhà cung cấp eKYC chưa thể đọc giấy tờ. Vui lòng chụp lại ảnh rõ hơn.");
            }

            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            var errorCode = GetString(root, "errorCode");
            if (errorCode is not ("0" or null))
            {
                return OcrFailure(GetString(root, "errorMessage") ?? "Không đọc được thông tin trên giấy tờ.");
            }

            var values = new Dictionary<string, (string? Value, decimal? Score)>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var key = GetString(item, "key");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    values[key] = (GetString(item, "value"), ParseDecimal(GetString(item, "score")));
                }
            }

            string? Find(params string[] keys)
            {
                foreach (var key in keys)
                {
                    if (values.TryGetValue(key, out var item) && !string.IsNullOrWhiteSpace(item.Value))
                    {
                        return item.Value.Trim();
                    }
                }
                return null;
            }

            var confidenceValues = values.Values
                .Where(item => item.Score.HasValue)
                .Select(item => item.Score!.Value)
                .ToArray();
            var confidence = confidenceValues.Length == 0
                ? (decimal?)null
                : Math.Round(confidenceValues.Average(), 2);

            var gender = NormalizeGender(Find("Sex", "Gender"));

            return new EkycOcrResult(
                true,
                false,
                ProviderLabel,
                sessionId,
                Find("ID", "Id", "Document ID", "License No", "License Number", "No"),
                Find("Name", "Full Name", "FullName"),
                ParseDate(Find("Date of birth", "DOB", "Birth Date")),
                gender,
                ParseDate(Find("Issue Date", "Issued Date", "Date of issue")),
                ParseDate(Find("Expired Date", "Expiry Date", "Date of expiry")),
                Find("Address", "Permanent Address", "Residence"),
                Find("Class", "License Class", "Rank", "Category"),
                confidence,
                "Đã đọc thông tin giấy tờ bằng OCR. Vui lòng kiểm tra lại trước khi gửi.",
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Không thể gọi FPT eKYC OCR cho document type {DocumentType}.", documentType);
            return OcrFailure("Không thể kết nối dịch vụ eKYC. Vui lòng thử lại hoặc dùng xác minh thủ công.");
        }
    }

    private async Task<string?> InitializeSessionAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("session/init"))
        {
            Content = JsonContent.Create(new
            {
                memory = "0",
                nfc_support = "false"
            })
        };
        request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("device-type", "web-sdk");
        request.Headers.TryAddWithoutValidation("client_uuid", Guid.NewGuid().ToString("D"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "FPT eKYC init session failed with HTTP {StatusCode}: {Payload}",
                response.StatusCode,
                TrimForLog(payload));
            return null;
        }

        using var json = JsonDocument.Parse(payload);
        return GetString(json.RootElement, "session-id");
    }

    private void AddProviderHeaders(HttpRequestMessage request, string sessionId)
    {
        request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("session-id", sessionId);
        request.Headers.TryAddWithoutValidation("device-type", "web-sdk");
    }

    private static void AddImage(MultipartFormDataContent form, IFormFile file, string fieldName)
    {
        var content = new StreamContent(file.OpenReadStream());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType);
        form.Add(content, fieldName, Path.GetFileName(file.FileName));
    }

    private string BuildUrl(string relativePath) =>
        $"{_options.BaseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";

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

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

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
        if (string.IsNullOrWhiteSpace(value))
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

    private static string TrimForLog(string value) =>
        value.Length <= 800 ? value : value[..800];
}
