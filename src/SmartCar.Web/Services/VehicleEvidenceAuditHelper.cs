using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Services;

public sealed record CustomerVehicleEvidenceSnapshot(
    DateTime RecordedAt,
    int Mileage,
    string FuelLevel,
    bool HasIssue,
    string? Note,
    IReadOnlyList<string> ImagePaths,
    string? EvidenceHash,
    string? IpAddress,
    string? UserAgent);

public static class VehicleEvidenceAuditHelper
{
    public const string CustomerCheckInCompleted = "CustomerVehicleCheckInCompleted";
    public const string CustomerCheckOutCompleted = "CustomerVehicleCheckOutCompleted";

    public static readonly string[] StandardPhotoLabels =
    {
        "Chính diện xe",
        "Phía sau xe",
        "Bên trái thân xe",
        "Bên phải thân xe",
        "ODO + nhiên liệu/pin",
        "Nội thất xe"
    };

    public static Task<CustomerVehicleEvidenceSnapshot?> GetCheckInAsync(
        ApplicationDbContext dbContext,
        int bookingId,
        CancellationToken cancellationToken = default) =>
        GetEvidenceAsync(dbContext, bookingId, CustomerCheckInCompleted, cancellationToken);

    public static Task<CustomerVehicleEvidenceSnapshot?> GetCheckOutAsync(
        ApplicationDbContext dbContext,
        int bookingId,
        CancellationToken cancellationToken = default) =>
        GetEvidenceAsync(dbContext, bookingId, CustomerCheckOutCompleted, cancellationToken);

    public static async Task<bool> HasCheckInAsync(
        ApplicationDbContext dbContext,
        int bookingId,
        CancellationToken cancellationToken = default) =>
        await dbContext.AuditLogs.AsNoTracking().AnyAsync(log =>
            log.Action == CustomerCheckInCompleted &&
            log.EntityId == bookingId.ToString(),
            cancellationToken);

    public static async Task<bool> HasCheckOutAsync(
        ApplicationDbContext dbContext,
        int bookingId,
        CancellationToken cancellationToken = default) =>
        await dbContext.AuditLogs.AsNoTracking().AnyAsync(log =>
            log.Action == CustomerCheckOutCompleted &&
            log.EntityId == bookingId.ToString(),
            cancellationToken);

    private static async Task<CustomerVehicleEvidenceSnapshot?> GetEvidenceAsync(
        ApplicationDbContext dbContext,
        int bookingId,
        string action,
        CancellationToken cancellationToken)
    {
        var log = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(item => item.Action == action && item.EntityId == bookingId.ToString())
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new
            {
                item.CreatedAt,
                item.NewValues,
                item.IpAddress
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (log is null || string.IsNullOrWhiteSpace(log.NewValues))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(log.NewValues);
            var root = document.RootElement;

            var mileage = root.TryGetProperty("Mileage", out var mileageElement) &&
                          mileageElement.TryGetInt32(out var mileageValue)
                ? mileageValue
                : 0;
            var fuelLevel = root.TryGetProperty("FuelLevel", out var fuelElement) &&
                            fuelElement.ValueKind == JsonValueKind.String
                ? fuelElement.GetString() ?? string.Empty
                : string.Empty;
            var hasIssue = root.TryGetProperty("HasIssue", out var issueElement) &&
                           issueElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                           issueElement.GetBoolean();
            var note = root.TryGetProperty("Note", out var noteElement) &&
                       noteElement.ValueKind == JsonValueKind.String
                ? Normalize(noteElement.GetString())
                : null;
            var imagePaths = root.TryGetProperty("ImagePaths", out var imagesElement) &&
                             imagesElement.ValueKind == JsonValueKind.String
                ? SplitPaths(imagesElement.GetString())
                : Array.Empty<string>();
            var evidenceHash = root.TryGetProperty("EvidenceHash", out var hashElement) &&
                               hashElement.ValueKind == JsonValueKind.String
                ? Normalize(hashElement.GetString())
                : null;
            var userAgent = root.TryGetProperty("UserAgent", out var userAgentElement) &&
                            userAgentElement.ValueKind == JsonValueKind.String
                ? Normalize(userAgentElement.GetString())
                : null;

            return new CustomerVehicleEvidenceSnapshot(
                log.CreatedAt,
                mileage,
                fuelLevel,
                hasIssue,
                note,
                imagePaths,
                evidenceHash,
                Normalize(log.IpAddress),
                userAgent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> SplitPaths(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
