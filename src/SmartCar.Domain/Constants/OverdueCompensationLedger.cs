namespace SmartCar.Domain.Constants;

/// <summary>
/// Stable identifiers used to connect an overdue renter's compensation ledger
/// to the booking that was affected by the late vehicle return.
/// </summary>
public static class OverdueCompensationLedger
{
    public const string DepositDeductionPrefix = "OVERDUE-COMP-";
    public const string DebtPrefix = "OVERDUE-DEBT-";
    public const string FundedRefundPrefix = "OVERDUE-FUNDED-";

    public static string BuildDepositDeductionRelationPrefix(
        int renterBookingId,
        int affectedBookingId) =>
        $"{DepositDeductionPrefix}{renterBookingId}-{affectedBookingId}-";

    public static string BuildDebtRelationPrefix(
        int renterBookingId,
        int affectedBookingId) =>
        $"{DebtPrefix}{renterBookingId}-{affectedBookingId}-";

    public static decimal CalculateRefundReleaseAmount(
        decimal fundedFromDeposit,
        decimal fundedFromDebt,
        decimal compensationRefundAlreadyRecorded)
    {
        var totalFunded =
            Math.Max(0m, fundedFromDeposit) +
            Math.Max(0m, fundedFromDebt);
        var alreadyRecorded = Math.Max(0m, compensationRefundAlreadyRecorded);

        return Math.Max(0m, totalFunded - alreadyRecorded);
    }

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

    public static string BuildFundedRefundCode(
        int renterBookingId,
        int affectedBookingId,
        DateTime occurredAtUtc) =>
        $"{FundedRefundPrefix}{renterBookingId}-{affectedBookingId}-{occurredAtUtc:yyyyMMddHHmmssfff}";

    public static bool IsFundedRefundCode(string? transactionCode) =>
        !string.IsNullOrWhiteSpace(transactionCode) &&
        transactionCode.StartsWith(
            FundedRefundPrefix,
            StringComparison.OrdinalIgnoreCase);

    public static bool TryParseFundedRefundRelation(
        string? transactionCode,
        out int renterBookingId,
        out int affectedBookingId) =>
        TryParseRelation(
            transactionCode,
            FundedRefundPrefix,
            out renterBookingId,
            out affectedBookingId);

    /// <summary>
    /// Compensation refunds created before relation-aware funded markers are
    /// treated conservatively as already recorded so legacy data cannot be
    /// paid twice. New funded markers only count for their exact A -> B pair.
    /// </summary>
    public static bool CountsTowardRefundRelation(
        string? transactionCode,
        int renterBookingId,
        int affectedBookingId)
    {
        if (!IsFundedRefundCode(transactionCode))
        {
            return true;
        }

        return TryParseFundedRefundRelation(
                   transactionCode,
                   out var recordedRenterBookingId,
                   out var recordedAffectedBookingId) &&
               recordedRenterBookingId == renterBookingId &&
               recordedAffectedBookingId == affectedBookingId;
    }

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
