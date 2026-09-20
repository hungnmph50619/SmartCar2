using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Constants;

// Values are persisted with each booking, independently of later configuration edits.
public sealed class RentalPolicySnapshot : IValidatableObject
{
    public string Version { get; set; } = "legacy";
    [Range(0, 30)] public int DepositHoldDays { get; set; } = 15;
    [Range(typeof(decimal), "1", "1000")] public decimal DepositPercent { get; set; } = 300m;
    [Range(1, 1000, ErrorMessage = "Định mức quãng đường phải từ 1 đến 1.000 km/ngày.")] public int IncludedKilometersPerDay { get; set; } = 300;
    [Range(typeof(decimal), "0", "50000", ErrorMessage = "Phí vượt km phải từ 0 đến 50.000 đ/km.")] public decimal ExcessKilometerFee { get; set; } = 5000m;
    [Range(typeof(decimal), "1", "5", ErrorMessage = "Hệ số trả muộn phải từ 1 đến 5 lần/ngày.")] public decimal LateReturnFeeMultiplier { get; set; } = 1.5m;
    [Range(0, 1440)] public int LateReturnGraceMinutes { get; set; }
    [Range(0d, 20d, ErrorMessage = "Quãng đường trong phí giao cơ bản phải từ 0 đến 20 km.")] public double IncludedDeliveryDistanceKm { get; set; } = 3d;
    [Range(typeof(decimal), "0", "100000", ErrorMessage = "Phí giao cơ bản phải từ 0 đến 100.000 đồng.")] public decimal BaseDeliveryFee { get; set; } = 30000m;
    [Range(typeof(decimal), "0", "50000", ErrorMessage = "Phí mỗi km tiếp theo phải từ 0 đến 50.000 đ/km.")] public decimal DeliveryFeePerExtraKm { get; set; } = 10000m;
    [Range(1d, 80d, ErrorMessage = "Phạm vi giao tối đa phải từ 1 đến 80 km.")] public double MaxDeliveryDistanceKm { get; set; } = 50d;
    [Required, StringLength(1500)] public string TrafficFineTerms { get; set; } = RentalPolicy.TrafficFineTerms;
    [Required, StringLength(1500)] public string DamageCompensationTerms { get; set; } = RentalPolicy.DamageCompensationTerms;

    public decimal CalculateDeposit(decimal rentalAmount) =>
        Math.Round(rentalAmount * DepositPercent / 100m, 0, MidpointRounding.AwayFromZero);

    public decimal CalculateDeliveryFee(VehiclePickupMethod method, decimal? latitude, decimal? longitude)
    {
        if (method != VehiclePickupMethod.Delivery || !latitude.HasValue || !longitude.HasValue) return 0m;
        return DeliveryFeeForDistance(RentalPolicy.CalculateDeliveryDistanceKm(latitude.Value, longitude.Value));
    }

    public decimal DeliveryFeeForDistance(double distanceKm) => BaseDeliveryFee +
        (decimal)Math.Ceiling(Math.Max(0d, distanceKm - IncludedDeliveryDistanceKm)) * DeliveryFeePerExtraKm;

    // Each started 24-hour period AFTER the grace period is charged as one day.
    public int LateChargeDays(int lateMinutes) =>
        (int)Math.Ceiling(Math.Max(0, lateMinutes - LateReturnGraceMinutes) / 1440d);

    public string ToJson() => JsonSerializer.Serialize(this);
    public static RentalPolicySnapshot FromJson(string? json) => string.IsNullOrWhiteSpace(json)
        ? new RentalPolicySnapshot()
        : JsonSerializer.Deserialize<RentalPolicySnapshot>(json)
            ?? throw new InvalidOperationException("Không đọc được chính sách của đơn.");

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!double.IsFinite(IncludedDeliveryDistanceKm) || !double.IsFinite(MaxDeliveryDistanceKm) ||
            IncludedDeliveryDistanceKm > MaxDeliveryDistanceKm)
            yield return new ValidationResult("Số km trong phí cơ bản không được lớn hơn bán kính giao tối đa.",
                new[] { nameof(IncludedDeliveryDistanceKm) });
        if (new[] { DepositPercent, LateReturnFeeMultiplier }.Any(x => decimal.Round(x, 2) != x))
            yield return new ValidationResult("Tỷ lệ và hệ số chỉ được có tối đa 2 chữ số thập phân.");
        if (new[] { BaseDeliveryFee, DeliveryFeePerExtraKm, ExcessKilometerFee }.Any(x => decimal.Truncate(x) != x))
            yield return new ValidationResult("Các khoản phí phải là số nguyên đồng.");
    }
}
