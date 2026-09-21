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

    // Giữ chỗ / thanh toán. Các giá trị này được snapshot vào booking để thay đổi sau này
    // không làm thay đổi thời hạn của đơn đã tạo.
    [Range(5, 240, ErrorMessage = "Thời gian chờ xác nhận phải từ 5 đến 240 phút.")]
    public int BookingConfirmationHoldMinutes { get; set; } = RentalPolicy.BookingConfirmationHoldMinutes;

    [Range(5, 180, ErrorMessage = "Thời gian chờ thanh toán phải từ 5 đến 180 phút.")]
    public int BookingPaymentHoldMinutes { get; set; } = RentalPolicy.BookingPaymentHoldMinutes;

    [Range(15, 720, ErrorMessage = "Thời gian chờ đối soát phải từ 15 đến 720 phút.")]
    public int BookingTransferReconciliationHoldMinutes { get; set; } = RentalPolicy.BookingTransferReconciliationHoldMinutes;

    [Range(0, 1440, ErrorMessage = "Thời gian đặt trước tối thiểu phải từ 0 đến 1.440 phút.")]
    public int MinimumPickupLeadMinutes { get; set; } = 5;

    // Khoảng vận hành giữa hai lượt thuê cùng xe.
    [Range(0, 360, ErrorMessage = "Thời gian xoay vòng xe phải từ 0 đến 360 phút.")]
    public int VehicleTurnaroundMinutes { get; set; } = RentalPolicy.VehicleTurnaroundMinutes;


    // Không đến nhận xe.
    [Range(0, 180, ErrorMessage = "Thời gian chờ khách đến nhận phải từ 0 đến 180 phút.")]
    public int NoShowGraceMinutes { get; set; } = RentalPolicy.NoShowGraceMinutes;

    [Range(typeof(decimal), "0", "100", ErrorMessage = "Tỷ lệ giữ tiền thuê khi No-show phải từ 0 đến 100%.")]
    public decimal NoShowFeePercent { get; set; } = RentalPolicy.NoShowFeeRate * 100m;

    // Hủy đơn / hoàn tiền thuê.
    [Range(0, 1440, ErrorMessage = "Cửa sổ hủy miễn phí phải từ 0 đến 1.440 phút.")]
    public int FreeCancellationWindowMinutes { get; set; } = 60;

    [Range(0, 336, ErrorMessage = "Điều kiện thời gian còn lại phải từ 0 đến 336 giờ.")]
    public int MinimumHoursForFreeCancellation { get; set; } = 24;

    // Tương thích booking cũ: JSON cũ không có field này => true.
    // Policy hiện hành cho đơn mới được BusinessPolicyStore chuyển thành false.
    public bool FreeCancellationRequiresMinimumLead { get; set; } = true;

    [Range(1, 168, ErrorMessage = "Thời gian xử lý hoàn tiền sau hủy phải từ 1 đến 168 giờ.")]
    public int CancellationRefundProcessingHours { get; set; } = 24;

    [Range(1, 720)] public int CancellationTier1Hours { get; set; } = 168;
    [Range(typeof(decimal), "0", "100")] public decimal CancellationTier1RefundPercent { get; set; } = 90m;
    [Range(1, 720)] public int CancellationTier2Hours { get; set; } = 48;
    [Range(typeof(decimal), "0", "100")] public decimal CancellationTier2RefundPercent { get; set; } = 70m;
    [Range(1, 720)] public int CancellationTier3Hours { get; set; } = 24;
    [Range(typeof(decimal), "0", "100")] public decimal CancellationTier3RefundPercent { get; set; } = 50m;
    [Range(1, 720)] public int CancellationTier4Hours { get; set; } = 6;
    [Range(typeof(decimal), "0", "100")] public decimal CancellationTier4RefundPercent { get; set; } = 20m;
    [Range(typeof(decimal), "0", "100")] public decimal CancellationBelowTierRefundPercent { get; set; } = 0m;
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

    public int GetOperationalPreparationMinutes(VehiclePickupMethod pickupMethod) =>
        VehicleTurnaroundMinutes;

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

        if (IncludedDeliveryDistanceKm != Math.Truncate(IncludedDeliveryDistanceKm) ||
            MaxDeliveryDistanceKm != Math.Truncate(MaxDeliveryDistanceKm))
        {
            yield return new ValidationResult(
                "Khoảng cách giao xe phải là số nguyên km.",
                new[] { nameof(IncludedDeliveryDistanceKm), nameof(MaxDeliveryDistanceKm) });
        }
        if (decimal.Truncate(DepositPercent) != DepositPercent)
            yield return new ValidationResult(
                "Tỷ lệ tiền cọc phải là số nguyên phần trăm.",
                new[] { nameof(DepositPercent) });

        if (decimal.Round(LateReturnFeeMultiplier, 2) != LateReturnFeeMultiplier)
            yield return new ValidationResult(
                "Hệ số trả muộn chỉ được có tối đa 2 chữ số thập phân.",
                new[] { nameof(LateReturnFeeMultiplier) });
        if (new[] { BaseDeliveryFee, DeliveryFeePerExtraKm, ExcessKilometerFee }.Any(x => decimal.Truncate(x) != x))
            yield return new ValidationResult("Các khoản phí phải là số nguyên đồng.");

        if (!(CancellationTier1Hours > CancellationTier2Hours &&
              CancellationTier2Hours > CancellationTier3Hours &&
              CancellationTier3Hours > CancellationTier4Hours))
        {
            yield return new ValidationResult(
                "Các mốc hủy phải giảm dần: mốc 1 > mốc 2 > mốc 3 > mốc 4.",
                new[]
                {
                    nameof(CancellationTier1Hours),
                    nameof(CancellationTier2Hours),
                    nameof(CancellationTier3Hours),
                    nameof(CancellationTier4Hours)
                });
        }

        if (!(CancellationTier1RefundPercent >= CancellationTier2RefundPercent &&
              CancellationTier2RefundPercent >= CancellationTier3RefundPercent &&
              CancellationTier3RefundPercent >= CancellationTier4RefundPercent &&
              CancellationTier4RefundPercent >= CancellationBelowTierRefundPercent))
        {
            yield return new ValidationResult(
                "Tỷ lệ hoàn tiền phải giảm dần theo thời điểm hủy.",
                new[]
                {
                    nameof(CancellationTier1RefundPercent),
                    nameof(CancellationTier2RefundPercent),
                    nameof(CancellationTier3RefundPercent),
                    nameof(CancellationTier4RefundPercent),
                    nameof(CancellationBelowTierRefundPercent)
                });
        }

        if (decimal.Truncate(NoShowFeePercent) != NoShowFeePercent)
        {
            yield return new ValidationResult(
                "Tỷ lệ giữ tiền thuê khi No-show phải là số nguyên phần trăm.",
                new[] { nameof(NoShowFeePercent) });
        }

        if (new[]
            {
                CancellationTier1RefundPercent,
                CancellationTier2RefundPercent,
                CancellationTier3RefundPercent,
                CancellationTier4RefundPercent,
                CancellationBelowTierRefundPercent
            }.Any(x => decimal.Truncate(x) != x))
        {
            yield return new ValidationResult(
                "Tỷ lệ hoàn tiền khi hủy phải là số nguyên phần trăm.",
                new[]
                {
                    nameof(CancellationTier1RefundPercent),
                    nameof(CancellationTier2RefundPercent),
                    nameof(CancellationTier3RefundPercent),
                    nameof(CancellationTier4RefundPercent),
                    nameof(CancellationBelowTierRefundPercent)
                });
        }
    }
}
