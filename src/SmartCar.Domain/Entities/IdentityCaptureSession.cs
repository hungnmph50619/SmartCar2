using SmartCar.Domain.Constants;

namespace SmartCar.Domain.Entities;

public sealed class IdentityCaptureSession
{
    public Guid IdentityCaptureSessionId { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = string.Empty;
    public string Purpose { get; set; } = IdentityCapturePurposes.Kyc;
    public string TargetCustomerId { get; set; } = string.Empty;
    public int? BookingId { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public string? ImagePath { get; set; }
    public string? CaptureMethod { get; set; }
    public string? FallbackReason { get; set; }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAt;

    public bool CanCapture(DateTime utcNow) =>
        !IsExpired(utcNow) && !CompletedAt.HasValue && string.IsNullOrWhiteSpace(ImagePath);

    public bool CanConsume(DateTime utcNow) =>
        !IsExpired(utcNow) && CompletedAt.HasValue && !ConsumedAt.HasValue && !string.IsNullOrWhiteSpace(ImagePath);
}
