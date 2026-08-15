using System.Text.Json;

namespace SmartCar.Web.Services;

public sealed record EarlyReturnSnapshot(
    string Status,
    DateTime RequestedReturnAt,
    string ReturnLocation,
    string Reason,
    string? DecisionNote,
    DateTime EventAt);

public sealed record RentalTerminationSnapshot(
    string Status,
    string ReasonType,
    string Reason,
    DateTime RequestedReturnAt,
    DateTime EventAt);

public static class RentalLifecycleAuditHelper
{
    public const string EarlyReturnRequested = "EarlyReturnRequested";
    public const string EarlyReturnApproved = "EarlyReturnApproved";
    public const string EarlyReturnRejected = "EarlyReturnRejected";
    public const string RentalTerminationRequested = "RentalTerminationRequested";
    public const string RentalTerminationCancelled = "RentalTerminationCancelled";

    public static readonly string[] EarlyReturnActions =
    {
        EarlyReturnRequested,
        EarlyReturnApproved,
        EarlyReturnRejected
    };

    public static readonly string[] RentalTerminationActions =
    {
        RentalTerminationRequested,
        RentalTerminationCancelled
    };

    public static string SerializeEarlyReturn(
        DateTime requestedReturnAt,
        string returnLocation,
        string reason,
        string? decisionNote = null) =>
        JsonSerializer.Serialize(new
        {
            RequestedReturnAt = requestedReturnAt,
            ReturnLocation = returnLocation,
            Reason = reason,
            DecisionNote = decisionNote
        });

    public static string SerializeTermination(
        string reasonType,
        string reason,
        DateTime requestedReturnAt) =>
        JsonSerializer.Serialize(new
        {
            ReasonType = reasonType,
            Reason = reason,
            RequestedReturnAt = requestedReturnAt
        });

    public static EarlyReturnSnapshot? ParseEarlyReturn(
        string action,
        string? newValues,
        DateTime createdAt)
    {
        if (string.IsNullOrWhiteSpace(newValues))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(newValues);
            var root = document.RootElement;
            if (!TryGetDateTime(root, "RequestedReturnAt", out var requestedReturnAt))
            {
                return null;
            }

            var status = action switch
            {
                EarlyReturnRequested => "Pending",
                EarlyReturnApproved => "Approved",
                EarlyReturnRejected => "Rejected",
                _ => "Unknown"
            };

            return new EarlyReturnSnapshot(
                status,
                requestedReturnAt,
                GetString(root, "ReturnLocation") ?? string.Empty,
                GetString(root, "Reason") ?? string.Empty,
                GetString(root, "DecisionNote"),
                createdAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static RentalTerminationSnapshot? ParseTermination(
        string action,
        string? newValues,
        DateTime createdAt)
    {
        if (string.IsNullOrWhiteSpace(newValues))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(newValues);
            var root = document.RootElement;
            if (!TryGetDateTime(root, "RequestedReturnAt", out var requestedReturnAt))
            {
                return null;
            }

            return new RentalTerminationSnapshot(
                action == RentalTerminationCancelled ? "Cancelled" : "Requested",
                GetString(root, "ReasonType") ?? string.Empty,
                GetString(root, "Reason") ?? string.Empty,
                requestedReturnAt,
                createdAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ToVietnameseTerminationReason(string reasonType) => reasonType switch
    {
        "CustomerViolation" => "Khách vi phạm nghiêm trọng điều khoản thuê",
        "VehicleSafety" => "Vấn đề an toàn hoặc kỹ thuật của xe",
        "MutualAgreement" => "Hai bên thỏa thuận kết thúc sớm",
        "Other" => "Lý do khác",
        _ => reasonType
    };

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetDateTime(
        JsonElement root,
        string propertyName,
        out DateTime value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               element.TryGetDateTime(out value);
    }
}
