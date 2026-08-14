namespace SmartCar.Domain.Constants;

public static class DeliveryConstants
{
    public const string SelfPickup = "SelfPickup";
    public const string HomeDelivery = "HomeDelivery";

    // Điểm giao nhận demo của SmartCar tại Đống Đa, Hà Nội.
    public const string SmartCarLocation = "Số 2 ngõ 95 Chùa Bộc, Đống Đa, Hà Nội";
    public const string SmartCarMapQuery = "2 ngõ 95 Chùa Bộc, Đống Đa, Hà Nội";

    public const decimal RatePerKm = 15000m;
    public const decimal MaxDistancePerLegKm = 15m;
}
