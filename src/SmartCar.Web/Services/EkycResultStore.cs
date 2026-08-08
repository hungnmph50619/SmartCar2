using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace SmartCar.Web.Services;

public sealed class FileEkycResultStore : IEkycResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _secureRoot;

    public FileEkycResultStore(IWebHostEnvironment environment)
    {
        _secureRoot = Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "SecureDocuments");
    }

    public async Task SaveOcrAsync(
        string customerId,
        EkycOcrResult result,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.SessionId))
        {
            return;
        }

        var folder = GetCustomerFolder(customerId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, GetSessionFileName(result.SessionId));
        await WriteJsonAsync(path, result, cancellationToken);
    }

    public async Task<EkycOcrResult?> GetOcrAsync(
        string customerId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var path = Path.Combine(GetCustomerFolder(customerId), GetSessionFileName(sessionId));
        return await ReadJsonAsync<EkycOcrResult>(path, cancellationToken);
    }

    public async Task SaveLatestSummaryAsync(
        string customerId,
        EkycVerificationSummary summary,
        CancellationToken cancellationToken = default)
    {
        var folder = GetCustomerFolder(customerId);
        Directory.CreateDirectory(folder);
        await WriteJsonAsync(
            Path.Combine(folder, "ekyc-latest.json"),
            summary,
            cancellationToken);
    }

    public Task<EkycVerificationSummary?> GetLatestSummaryAsync(
        string customerId,
        CancellationToken cancellationToken = default) =>
        ReadJsonAsync<EkycVerificationSummary>(
            Path.Combine(GetCustomerFolder(customerId), "ekyc-latest.json"),
            cancellationToken);

    private async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                JsonOptions,
                cancellationToken);
        }

        File.Move(tempPath, path, true);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private string GetCustomerFolder(string customerId) =>
        Path.Combine(_secureRoot, SanitizeSegment(customerId));

    private static string GetSessionFileName(string sessionId) =>
        $"ekyc-session-{SanitizeSegment(sessionId)}.json";

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Where(character => !invalid.Contains(character) && character is not '/' and not '\\')
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
