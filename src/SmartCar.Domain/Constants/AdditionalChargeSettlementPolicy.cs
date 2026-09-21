namespace SmartCar.Domain.Constants;

public static class AdditionalChargeSettlementPolicy
{
    public const string ReadyMarkerPrefix = "ADD-READY-";

    public static string CreateReadyMarker(int bookingId, DateTime utcNow) =>
        $"{ReadyMarkerPrefix}{bookingId}-{utcNow:yyyyMMddHHmmssfff}";

    public static bool IsReadyMarker(string? transactionCode) =>
        !string.IsNullOrWhiteSpace(transactionCode) &&
        transactionCode.StartsWith(ReadyMarkerPrefix, StringComparison.Ordinal);
}
