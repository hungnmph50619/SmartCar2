using Microsoft.AspNetCore.Http;

namespace SmartCar.Web.Services;

public sealed class EkycOptions
{
    public string Mode { get; set; } = "Auto";
    public string Provider { get; set; } = "FPT.AI Reader";
    public string CitizenIdOcrUrl { get; set; } = "https://api.fpt.ai/vision/idr/vnm/";
    public string DrivingLicenseOcrUrl { get; set; } = "https://api.fpt.ai/vision/dlr/vnm";
    public string LivenessUrl { get; set; } = "https://api.fpt.ai/dmp/liveness/v3";
    public string ApiKey { get; set; } = string.Empty;
    public decimal FaceMatchThreshold { get; set; } = 80m;
}

public sealed record EkycOcrResult(
    bool Succeeded,
    bool IsDemo,
    string Provider,
    string? SessionId,
    string? DocumentNumber,
    string? FullName,
    DateTime? DateOfBirth,
    string? Gender,
    DateTime? IssuedDate,
    DateTime? ExpiryDate,
    string? Address,
    string? LicenseClass,
    decimal? OcrConfidence,
    string Message,
    DateTime CheckedAtUtc);

public sealed record EkycFaceVerificationResult(
    bool Succeeded,
    bool IsDemo,
    string Provider,
    bool? IsLive,
    bool? FaceMatched,
    decimal? Similarity,
    string Message,
    DateTime CheckedAtUtc);

public sealed record EkycVerificationSummary(
    string Provider,
    bool IsDemo,
    bool OcrSucceeded,
    decimal? OcrConfidence,
    bool? LivenessPassed,
    bool? FaceMatched,
    decimal? FaceSimilarity,
    bool RequiresManualReview,
    string Message,
    DateTime CheckedAtUtc);

public interface IEkycService
{
    string ProviderLabel { get; }
    bool IsDemoMode { get; }

    Task<EkycOcrResult> ReadCitizenIdAsync(
        IFormFile frontImage,
        IFormFile backImage,
        CancellationToken cancellationToken = default);

    Task<EkycOcrResult> ReadDrivingLicenseAsync(
        IFormFile frontImage,
        IFormFile backImage,
        CancellationToken cancellationToken = default);

    Task<EkycFaceVerificationResult> VerifyFaceAsync(
        IFormFile citizenFrontImage,
        IFormFile selfieVideo,
        CancellationToken cancellationToken = default);
}

public interface IEkycResultStore
{
    Task SaveOcrAsync(
        string customerId,
        EkycOcrResult result,
        CancellationToken cancellationToken = default);

    Task<EkycOcrResult?> GetOcrAsync(
        string customerId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SaveLatestSummaryAsync(
        string customerId,
        EkycVerificationSummary summary,
        CancellationToken cancellationToken = default);

    Task<EkycVerificationSummary?> GetLatestSummaryAsync(
        string customerId,
        CancellationToken cancellationToken = default);
}
