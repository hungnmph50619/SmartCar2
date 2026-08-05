using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public static class AdminDisplayHelper
{
    public static string BookingLabel(BookingStatus status) => status switch
    {
        BookingStatus.PendingConfirmation => "Chờ xác nhận",
        BookingStatus.PendingPayment => "Chờ thanh toán cọc",
        BookingStatus.Paid => "Đã thanh toán",
        BookingStatus.ReadyForPickup => "Sẵn sàng giao xe",
        BookingStatus.Rented => "Đang thuê",
        BookingStatus.PendingInspection => "Chờ kiểm tra trả xe",
        BookingStatus.Completed => "Hoàn thành",
        BookingStatus.Rejected => "Đã từ chối",
        BookingStatus.Cancelled => "Đã hủy",
        _ => status.ToString()
    };

    public static string BookingCss(BookingStatus status) => status switch
    {
        BookingStatus.PendingConfirmation => "pending",
        BookingStatus.PendingPayment => "payment",
        BookingStatus.Paid => "paid",
        BookingStatus.ReadyForPickup => "ready",
        BookingStatus.Rented => "rented",
        BookingStatus.PendingInspection => "inspection",
        BookingStatus.Completed => "completed",
        BookingStatus.Rejected => "rejected",
        BookingStatus.Cancelled => "cancelled",
        _ => "info"
    };

    public static string PaymentLabel(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => "Chờ thanh toán",
        PaymentStatus.Paid => "Đã thanh toán",
        PaymentStatus.Failed => "Thất bại",
        PaymentStatus.Refunded => "Đã hoàn tiền",
        _ => status.ToString()
    };

    public static string PaymentCss(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => "warning",
        PaymentStatus.Paid => "paid",
        PaymentStatus.Failed => "failed",
        PaymentStatus.Refunded => "refunded",
        _ => "info"
    };

    public static string VehicleLabel(VehicleStatus status) => status switch
    {
        VehicleStatus.Available => "Sẵn sàng",
        VehicleStatus.Rented => "Đang được thuê",
        VehicleStatus.Maintenance => "Đang bảo trì",
        VehicleStatus.Inactive => "Ngừng hoạt động",
        _ => status.ToString()
    };

    public static string VehicleCss(VehicleStatus status) => status switch
    {
        VehicleStatus.Available => "available",
        VehicleStatus.Rented => "rented",
        VehicleStatus.Maintenance => "maintenance",
        VehicleStatus.Inactive => "inactive",
        _ => "info"
    };

    public static string DocumentLabel(DocumentStatus status) => status switch
    {
        DocumentStatus.Pending => "Chờ xác minh",
        DocumentStatus.Verified => "Đã xác minh",
        DocumentStatus.Rejected => "Cần bổ sung",
        _ => status.ToString()
    };

    public static string DocumentCss(DocumentStatus status) => status switch
    {
        DocumentStatus.Pending => "pending",
        DocumentStatus.Verified => "verified",
        DocumentStatus.Rejected => "rejected",
        _ => "info"
    };

    public static string MaintenanceLabel(MaintenanceStatus status) => status switch
    {
        MaintenanceStatus.InProgress => "Đang thực hiện",
        MaintenanceStatus.Completed => "Đã hoàn thành",
        MaintenanceStatus.Cancelled => "Đã hủy",
        _ => status.ToString()
    };

    public static string MaintenanceCss(MaintenanceStatus status) => status switch
    {
        MaintenanceStatus.InProgress => "maintenance",
        MaintenanceStatus.Completed => "completed",
        MaintenanceStatus.Cancelled => "cancelled",
        _ => "info"
    };

    public static string DocumentTypeLabel(string documentType) => documentType.ToUpperInvariant() switch
    {
        "CCCD_FRONT" => "CCCD mặt trước",
        "CCCD_BACK" => "CCCD mặt sau",
        "DRIVER_LICENSE" => "Giấy phép lái xe",
        _ => documentType
    };
}
