using System.Globalization;
using System.Text.Json;

namespace SmartCar.Web.Services;

public sealed record BusinessPolicyAuditChange(
    string Key,
    string Label,
    string OldValue,
    string NewValue,
    bool IsLongText = false);

public static class BusinessPolicyAuditFormatter
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private sealed record FieldSpec(
        string Key,
        string Label,
        Func<JsonElement, string> Format,
        bool IsLongText = false);

    private static readonly FieldSpec[] Fields =
    {
        new("DepositPercent", "Tỷ lệ cọc", value => $"{FormatDecimal(value)}%"),
        new("DepositHoldDays", "Giữ cọc", value => $"{FormatInteger(value)} ngày"),
        new("IncludedKilometersPerDay", "Định mức quãng đường", value => $"{FormatInteger(value)} km/ngày"),
        new("ExcessKilometerFee", "Phí vượt km", value => $"{FormatMoney(value)} đ/km"),
        new("LateReturnFeeMultiplier", "Hệ số trả muộn", value => $"{FormatDecimal(value)} lần/ngày"),
        new("LateReturnGraceMinutes", "Miễn phí trả muộn", value => $"{FormatInteger(value)} phút"),
        new("IncludedDeliveryDistanceKm", "Km trong phí giao", value => $"{FormatDecimal(value)} km"),
        new("BaseDeliveryFee", "Phí giao cơ bản", value => $"{FormatMoney(value)} đ"),
        new("DeliveryFeePerExtraKm", "Phí mỗi km giao thêm", value => $"{FormatMoney(value)} đ/km"),
        new("MaxDeliveryDistanceKm", "Phạm vi giao tối đa", value => $"{FormatDecimal(value)} km"),
        new("BookingConfirmationHoldMinutes", "Chờ xác nhận đơn", value => $"{FormatInteger(value)} phút"),
        new("BookingPaymentHoldMinutes", "Chờ khách thanh toán", value => $"{FormatInteger(value)} phút"),
        new("BookingTransferReconciliationHoldMinutes", "Chờ đối soát chuyển khoản", value => $"{FormatInteger(value)} phút"),
        new("MinimumPickupLeadMinutes", "Đặt xe trước tối thiểu", value => $"{FormatInteger(value)} phút"),
        new("VehicleTurnaroundMinutes", "Xoay vòng giữa hai lượt thuê", value => $"{FormatInteger(value)} phút"),
        new("NoShowGraceMinutes", "Chờ khách đến nhận", value => $"{FormatInteger(value)} phút"),
        new("NoShowFeePercent", "Tỷ lệ giữ tiền thuê khi No-show", value => $"{FormatDecimal(value)}%"),
        new("FreeCancellationWindowMinutes", "Cửa sổ hủy miễn phí", value => $"{FormatInteger(value)} phút"),
        new("MinimumHoursForFreeCancellation", "Còn tối thiểu để hoàn 100%", value => $"{FormatInteger(value)} giờ"),
        new("CancellationRefundProcessingHours", "Xử lý hoàn tiền sau hủy", value => $"{FormatInteger(value)} giờ"),
        new("CancellationTier1Hours", "Mốc hủy 1", value => $"{FormatInteger(value)} giờ"),
        new("CancellationTier1RefundPercent", "Hoàn ở mốc 1", value => $"{FormatDecimal(value)}%"),
        new("CancellationTier2Hours", "Mốc hủy 2", value => $"{FormatInteger(value)} giờ"),
        new("CancellationTier2RefundPercent", "Hoàn ở mốc 2", value => $"{FormatDecimal(value)}%"),
        new("CancellationTier3Hours", "Mốc hủy 3", value => $"{FormatInteger(value)} giờ"),
        new("CancellationTier3RefundPercent", "Hoàn ở mốc 3", value => $"{FormatDecimal(value)}%"),
        new("CancellationTier4Hours", "Mốc hủy 4", value => $"{FormatInteger(value)} giờ"),
        new("CancellationTier4RefundPercent", "Hoàn ở mốc 4", value => $"{FormatDecimal(value)}%"),
        new("CancellationBelowTierRefundPercent", "Hoàn dưới mốc cuối", value => $"{FormatDecimal(value)}%"),
        new("TrafficFineTerms", "Điều khoản phạt nguội", FormatText, true),
        new("DamageCompensationTerms", "Điều khoản hư hỏng / bồi thường", FormatText, true)
    };

    public static IReadOnlyList<BusinessPolicyAuditChange> GetChanges(
        string? oldValues,
        string? newValues)
    {
        if (string.IsNullOrWhiteSpace(oldValues) || string.IsNullOrWhiteSpace(newValues))
        {
            return Array.Empty<BusinessPolicyAuditChange>();
        }

        try
        {
            using var oldDocument = JsonDocument.Parse(oldValues);
            using var newDocument = JsonDocument.Parse(newValues);

            if (oldDocument.RootElement.ValueKind != JsonValueKind.Object ||
                newDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<BusinessPolicyAuditChange>();
            }

            var changes = new List<BusinessPolicyAuditChange>();

            foreach (var field in Fields)
            {
                if (!oldDocument.RootElement.TryGetProperty(field.Key, out var oldValue) ||
                    !newDocument.RootElement.TryGetProperty(field.Key, out var newValue) ||
                    AreEquivalent(oldValue, newValue))
                {
                    continue;
                }

                changes.Add(new BusinessPolicyAuditChange(
                    field.Key,
                    field.Label,
                    field.Format(oldValue),
                    field.Format(newValue),
                    field.IsLongText));
            }

            return changes;
        }
        catch (JsonException)
        {
            return Array.Empty<BusinessPolicyAuditChange>();
        }
    }

    public static string BuildSummary(string? oldValues, string? newValues)
    {
        var changes = GetChanges(oldValues, newValues);
        if (changes.Count == 0)
        {
            return "Không có thay đổi giá trị";
        }

        return string.Join(
            " · ",
            changes.Select(change =>
                change.IsLongText
                    ? $"{change.Label}: đã cập nhật"
                    : $"{change.Label}: {change.OldValue} → {change.NewValue}"));
    }

    private static bool AreEquivalent(JsonElement oldValue, JsonElement newValue)
    {
        if (oldValue.ValueKind == JsonValueKind.Number &&
            newValue.ValueKind == JsonValueKind.Number &&
            oldValue.TryGetDecimal(out var oldNumber) &&
            newValue.TryGetDecimal(out var newNumber))
        {
            return oldNumber == newNumber;
        }

        if (oldValue.ValueKind == JsonValueKind.String &&
            newValue.ValueKind == JsonValueKind.String)
        {
            return string.Equals(
                oldValue.GetString()?.Trim(),
                newValue.GetString()?.Trim(),
                StringComparison.Ordinal);
        }

        return oldValue.GetRawText() == newValue.GetRawText();
    }

    private static string FormatInteger(JsonElement value)
    {
        if (value.TryGetInt64(out var integer))
        {
            return integer.ToString("N0", Vi);
        }

        return FormatDecimal(value);
    }

    private static string FormatMoney(JsonElement value)
    {
        if (value.TryGetDecimal(out var number))
        {
            return number.ToString("N0", Vi);
        }

        return value.ToString();
    }

    private static string FormatDecimal(JsonElement value)
    {
        if (value.TryGetDecimal(out var number))
        {
            return number.ToString("0.##", Vi);
        }

        return value.ToString();
    }

    private static string FormatText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : value.ToString();
}
