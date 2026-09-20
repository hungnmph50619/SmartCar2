namespace SmartCar.Web.ViewModels;

public static class BusinessPolicyGroups
{
    public static string[] Fields(string? group) => group switch
    {
        "deposit" => new[] { "DepositPercent", "DepositHoldDays" },
        "mileage" => new[] { "IncludedKilometersPerDay", "ExcessKilometerFee" },
        "late" => new[] { "LateReturnFeeMultiplier", "LateReturnGraceMinutes" },
        "delivery" => new[] { "IncludedDeliveryDistanceKm", "BaseDeliveryFee", "DeliveryFeePerExtraKm", "MaxDeliveryDistanceKm" },
        "reservation" => new[] { "BookingConfirmationHoldMinutes", "BookingPaymentHoldMinutes", "BookingTransferReconciliationHoldMinutes", "MinimumPickupLeadMinutes" },
        "schedule" => new[] { "VehicleTurnaroundMinutes", "DeliveryLeadMinutes" },
        "cancellation" => new[]
        {
            "FreeCancellationWindowMinutes", "MinimumHoursForFreeCancellation",
            "CancellationTier1Hours", "CancellationTier1RefundPercent",
            "CancellationTier2Hours", "CancellationTier2RefundPercent",
            "CancellationTier3Hours", "CancellationTier3RefundPercent",
            "CancellationTier4Hours", "CancellationTier4RefundPercent",
            "CancellationBelowTierRefundPercent"
        },
        "noshow" => new[] { "NoShowGraceMinutes", "NoShowFeePercent" },
        "terms" => new[] { "TrafficFineTerms", "DamageCompensationTerms" },
        _ => Array.Empty<string>()
    };

    public static string Title(string group) => group switch
    {
        "deposit" => "Tiền cọc", "mileage" => "Quãng đường", "late" => "Trả muộn",
        "delivery" => "Giao xe tận nơi", "reservation" => "Giữ chỗ & thanh toán",
        "schedule" => "Lịch xe", "cancellation" => "Hủy đơn & hoàn tiền",
        "noshow" => "Không đến nhận xe", "terms" => "Điều khoản biên bản", _ => ""
    };
}
