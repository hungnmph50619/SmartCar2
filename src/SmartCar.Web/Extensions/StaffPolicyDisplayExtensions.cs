using SmartCar.Domain.Enums;

namespace SmartCar.Web.Extensions;

public static class StaffPolicyDisplayExtensions
{
    // Overload cụ thể hơn Enum.ToVietnamese hiện có, giúp trạng thái mới hiển thị tiếng Việt
    // mà không làm thay đổi các mapping enum khác của dự án.
    public static string ToVietnamese(this BookingStatus status) => status switch
    {
        BookingStatus.PendingConfirmation => "Chờ xác nhận",
        BookingStatus.Rejected => "Đã từ chối",
        BookingStatus.PendingPayment => "Chờ thanh toán",
        BookingStatus.Paid => "Đã thanh toán",
        BookingStatus.ReadyForPickup => "Sẵn sàng giao xe",
        BookingStatus.Rented => "Đang thuê",
        BookingStatus.PendingInspection => "Chờ kiểm tra xe",
        BookingStatus.AwaitingRefund => "Chờ hoàn tiền",
        BookingStatus.Completed => "Đã hoàn tất",
        BookingStatus.Cancelled => "Đã hủy",
        BookingStatus.NoShow => "Khách không đến nhận xe",
        BookingStatus.Expired => "Đã hết thời gian giữ chỗ",
        _ => status.ToString()
    };

    public static string ToVietnamese(this PaymentType type) => type switch
    {
        PaymentType.Deposit => "Tiền cọc",
        PaymentType.Rental => "Tiền thuê xe",
        PaymentType.AdditionalCharge => "Phụ phí",
        PaymentType.Refund => "Hoàn tiền",
        PaymentType.Extension => "Tiền gia hạn",
        PaymentType.VehicleSwapAdjustment => "Chênh lệch đổi xe",
        PaymentType.TrafficFine => "Phạt / vi phạm giao thông",
        _ => type.ToString()
    };
}
