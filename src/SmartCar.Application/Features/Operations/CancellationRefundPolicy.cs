namespace SmartCar.Application.Features.Operations;

/// <summary>
/// Chính sách hoàn tiền thuê khi khách chủ động hủy trước thời điểm nhận xe.
/// Tiền cọc và phí giao chưa thực hiện được xử lý riêng ở BookingOperationService.
/// </summary>
public static class CancellationRefundPolicy
{
    public const int FreeCancellationWindowMinutes = 60;
    public const int MinimumHoursForFreeCancellation = 24;

    /// <summary>
    /// Trả về tỷ lệ hoàn phần tiền thuê (0..1) khi khách chủ động hủy.
    /// </summary>
    public static decimal GetRentalRefundRate(
        DateTime cancelledAt,
        DateTime pickupDate,
        DateTime? rentalPaidAt)
    {
        if (pickupDate <= cancelledAt)
        {
            return 0m;
        }

        var hoursBeforePickup = (pickupDate - cancelledAt).TotalHours;

        if (rentalPaidAt.HasValue &&
            hoursBeforePickup >= MinimumHoursForFreeCancellation &&
            cancelledAt >= rentalPaidAt.Value &&
            (cancelledAt - rentalPaidAt.Value).TotalMinutes <= FreeCancellationWindowMinutes)
        {
            return 1.00m;
        }

        if (hoursBeforePickup >= 24 * 7)
        {
            return 0.90m;
        }

        if (hoursBeforePickup >= 48)
        {
            return 0.70m;
        }

        if (hoursBeforePickup >= 24)
        {
            return 0.50m;
        }

        if (hoursBeforePickup >= 6)
        {
            return 0.20m;
        }

        return 0m;
    }

    public static string GetVietnameseDescription(decimal refundRate) => refundRate switch
    {
        1.00m => "Hủy trong 60 phút sau khi thanh toán và còn ít nhất 24 giờ trước giờ nhận xe: hoàn 100% tiền thuê.",
        0.90m => "Hủy trước giờ nhận từ 7 ngày trở lên: hoàn 90% tiền thuê.",
        0.70m => "Hủy trước giờ nhận từ 48 giờ đến dưới 7 ngày: hoàn 70% tiền thuê.",
        0.50m => "Hủy trước giờ nhận từ 24 giờ đến dưới 48 giờ: hoàn 50% tiền thuê.",
        0.20m => "Hủy trước giờ nhận từ 6 giờ đến dưới 24 giờ: hoàn 20% tiền thuê.",
        _ => "Hủy trước giờ nhận dưới 6 giờ: không hoàn tiền thuê."
    };
}
