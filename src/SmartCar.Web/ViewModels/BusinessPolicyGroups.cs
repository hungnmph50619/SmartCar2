namespace SmartCar.Web.ViewModels;

public static class BusinessPolicyGroups
{
    public static string[] Fields(string? group) => group switch
    {
        "deposit" => new[] { "DepositPercent", "DepositHoldDays" },
        "mileage" => new[] { "IncludedKilometersPerDay", "ExcessKilometerFee" },
        "late" => new[] { "LateReturnFeeMultiplier", "LateReturnGraceMinutes" },
        "delivery" => new[] { "IncludedDeliveryDistanceKm", "BaseDeliveryFee", "DeliveryFeePerExtraKm", "MaxDeliveryDistanceKm" },
        "terms" => new[] { "TrafficFineTerms", "DamageCompensationTerms" },
        _ => Array.Empty<string>()
    };

    public static string Title(string group) => group switch
    {
        "deposit" => "Tiền cọc", "mileage" => "Quãng đường", "late" => "Trả muộn",
        "delivery" => "Giao xe tận nơi", "terms" => "Điều khoản biên bản", _ => ""
    };
}
