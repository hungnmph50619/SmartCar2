using SmartCar.Domain.Constants;

namespace SmartCar.Application.Features.Operations;

/// <summary>
/// Chính sách hoàn tiền thuê khi khách chủ động hủy trước thời điểm nhận xe.
/// Tiền cọc và phí giao chưa thực hiện được xử lý riêng ở BookingOperationService.
/// </summary>
public static class CancellationRefundPolicy
{
    public const int FreeCancellationWindowMinutes = 60;
    public const int MinimumHoursForFreeCancellation = 24;

    public static decimal GetRentalRefundRate(
        DateTime cancelledAt,
        DateTime pickupDate,
        DateTime? rentalPaidAt) =>
        CalculateRentalRefundRate(
            cancelledAt,
            pickupDate,
            rentalPaidAt,
            FreeCancellationWindowMinutes,
            MinimumHoursForFreeCancellation,
            168, 0.90m,
            48, 0.70m,
            24, 0.50m,
            6, 0.20m,
            0m);

    public static decimal GetRentalRefundRate(
        DateTime cancelledAt,
        DateTime pickupDate,
        DateTime? rentalPaidAt,
        RentalPolicySnapshot policy) =>
        CalculateRentalRefundRate(
            cancelledAt,
            pickupDate,
            rentalPaidAt,
            policy.FreeCancellationWindowMinutes,
            policy.MinimumHoursForFreeCancellation,
            policy.CancellationTier1Hours, policy.CancellationTier1RefundPercent / 100m,
            policy.CancellationTier2Hours, policy.CancellationTier2RefundPercent / 100m,
            policy.CancellationTier3Hours, policy.CancellationTier3RefundPercent / 100m,
            policy.CancellationTier4Hours, policy.CancellationTier4RefundPercent / 100m,
            policy.CancellationBelowTierRefundPercent / 100m);

    private static decimal CalculateRentalRefundRate(
        DateTime cancelledAt,
        DateTime pickupDate,
        DateTime? rentalPaidAt,
        int freeWindowMinutes,
        int minimumHoursForFreeCancellation,
        int tier1Hours,
        decimal tier1Rate,
        int tier2Hours,
        decimal tier2Rate,
        int tier3Hours,
        decimal tier3Rate,
        int tier4Hours,
        decimal tier4Rate,
        decimal belowTierRate)
    {
        if (pickupDate <= cancelledAt)
        {
            return 0m;
        }

        var hoursBeforePickup = (pickupDate - cancelledAt).TotalHours;

        if (rentalPaidAt.HasValue &&
            hoursBeforePickup >= minimumHoursForFreeCancellation &&
            cancelledAt >= rentalPaidAt.Value &&
            (cancelledAt - rentalPaidAt.Value).TotalMinutes <= freeWindowMinutes)
        {
            return 1.00m;
        }

        if (hoursBeforePickup >= tier1Hours) return tier1Rate;
        if (hoursBeforePickup >= tier2Hours) return tier2Rate;
        if (hoursBeforePickup >= tier3Hours) return tier3Rate;
        if (hoursBeforePickup >= tier4Hours) return tier4Rate;
        return belowTierRate;
    }

    public static string GetVietnamesePolicySummary() =>
        "Hoàn tiền thuê theo thời điểm hủy: trong 60 phút sau thanh toán và còn ít nhất 24 giờ trước giờ nhận: 100%; " +
        "từ 7 ngày trở lên: 90%; từ 48 giờ đến dưới 7 ngày: 70%; từ 24 đến dưới 48 giờ: 50%; " +
        "từ 6 đến dưới 24 giờ: 20%; dưới 6 giờ: 0%.";

    public static string GetVietnamesePolicySummary(RentalPolicySnapshot policy) =>
        $"Hoàn 100% nếu hủy trong {policy.FreeCancellationWindowMinutes} phút sau thanh toán và còn ít nhất {policy.MinimumHoursForFreeCancellation} giờ trước nhận; " +
        $"còn từ {policy.CancellationTier1Hours} giờ: {policy.CancellationTier1RefundPercent:0.##}%; " +
        $"từ {policy.CancellationTier2Hours} giờ: {policy.CancellationTier2RefundPercent:0.##}%; " +
        $"từ {policy.CancellationTier3Hours} giờ: {policy.CancellationTier3RefundPercent:0.##}%; " +
        $"từ {policy.CancellationTier4Hours} giờ: {policy.CancellationTier4RefundPercent:0.##}%; " +
        $"dưới mốc cuối: {policy.CancellationBelowTierRefundPercent:0.##}%.";

    public static string GetVietnameseDescription(decimal refundRate) => refundRate switch
    {
        1.00m => "Hủy trong 60 phút sau khi thanh toán và còn ít nhất 24 giờ trước giờ nhận xe: hoàn 100% tiền thuê.",
        0.90m => "Hủy trước giờ nhận từ 7 ngày trở lên: hoàn 90% tiền thuê.",
        0.70m => "Hủy trước giờ nhận từ 48 giờ đến dưới 7 ngày: hoàn 70% tiền thuê.",
        0.50m => "Hủy trước giờ nhận từ 24 đến dưới 48 giờ: hoàn 50% tiền thuê.",
        0.20m => "Hủy trước giờ nhận từ 6 đến dưới 24 giờ: hoàn 20% tiền thuê.",
        _ => "Hủy trước giờ nhận dưới 6 giờ: không hoàn tiền thuê."
    };

    public static string GetVietnameseDescription(
        decimal refundRate,
        RentalPolicySnapshot policy) =>
        $"Theo chính sách của đơn tại thời điểm hủy: hoàn {(refundRate * 100m):0.##}% tiền thuê.";
}
