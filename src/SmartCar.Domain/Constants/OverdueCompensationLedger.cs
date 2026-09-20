namespace SmartCar.Domain.Constants;

/// <summary>
/// Stable identifiers used to connect an overdue renter's compensation ledger
/// to the booking that was affected by the late vehicle return.
/// </summary>
public static class OverdueCompensationLedger
{
    public const string DepositDeductionPrefix = "OVERDUE-COMP-";
    public const string DebtPrefix = "OVERDUE-DEBT-";

    public static string BuildDepositDeductionCode(
        int renterBookingId,
        int affectedBookingId,
        DateTime occurredAtUtc) =>
        $"{DepositDeductionPrefix}{renterBookingId}-{affectedBookingId}-{occurredAtUtc:yyyyMMddHHmmss}";

    public static string BuildDebtCode(
        int renterBookingId,
        int affectedBookingId,
        DateTime occurredAtUtc) =>
        $"{DebtPrefix}{renterBookingId}-{affectedBookingId}-{occurredAtUtc:yyyyMMddHHmmss}";

    public static bool TryParseDebtRelation(
        string? transactionCode,
        out int renterBookingId,
        out int affectedBookingId) =>
        TryParseRelation(
            transactionCode,
            DebtPrefix,
            out renterBookingId,
            out affectedBookingId);

    public static bool TryParseDepositDeductionRelation(
        string? transactionCode,
        out int renterBookingId,
        out int affectedBookingId) =>
        TryParseRelation(
            transactionCode,
            DepositDeductionPrefix,
            out renterBookingId,
            out affectedBookingId);

    private static bool TryParseRelation(
        string? transactionCode,
        string prefix,
        out int renterBookingId,
        out int affectedBookingId)
    {
        renterBookingId = 0;
        affectedBookingId = 0;

        if (string.IsNullOrWhiteSpace(transactionCode) ||
            !transactionCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = transactionCode[prefix.Length..]
            .Split('-', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 3 &&
               int.TryParse(parts[0], out renterBookingId) &&
               renterBookingId > 0 &&
               int.TryParse(parts[1], out affectedBookingId) &&
               affectedBookingId > 0;
    }
}
