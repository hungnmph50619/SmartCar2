namespace SmartCar.Domain.Constants;

/// <summary>
/// Marker chỉ dùng để nhận diện dữ liệu thử nghiệm cũ từng tự động gán
/// mức bồi thường cho gia hạn bất khả kháng. Dữ liệu mới không dùng marker này:
/// quản trị viên phải nhập thiệt hại thực tế có căn cứ và hệ thống ghi nhận
/// khoản khấu trừ cọc bằng giao dịch riêng. Vì vậy marker cũ không còn giữ cọc.
/// </summary>
public static class CompensationLedger
{
    public const string Marker = "[NEXT_BOOKING_COMPENSATION]";
    public const string FundedRefundPrefix = "COMP-FUNDED-";

    public static decimal SumReservedAmount(string? value) => 0m;

    public static string BuildFundedRefundReference(
        string sourceKind,
        int sourceBookingId,
        int affectedBookingId,
        DateTime recordedAt)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(sourceKind)
            ? "GEN"
            : new string(sourceKind
                .Trim()
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            normalizedSource = "GEN";
        }

        return $"{FundedRefundPrefix}{normalizedSource}-{sourceBookingId}-{affectedBookingId}-{recordedAt:yyyyMMddHHmmssfff}";
    }

    public static bool IsFundedRefund(
        string? ledgerReference,
        string? transactionCode) =>
        IsGenericFundedReference(ledgerReference) ||
        OverdueCompensationLedger.IsFundedRefundCode(ledgerReference) ||
        OverdueCompensationLedger.IsFundedRefundCode(transactionCode);

    public static bool IsGenericFundedReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith(
            FundedRefundPrefix,
            StringComparison.OrdinalIgnoreCase);
}
