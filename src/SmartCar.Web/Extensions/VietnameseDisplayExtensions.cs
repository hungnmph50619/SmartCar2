using SmartCar.Domain.Enums;

namespace SmartCar.Web.Extensions;

public static class VietnameseDisplayExtensions
{
    public static string ToVietnamese(this Enum value) => value switch
    {
        BookingStatus status => status switch
        {
            BookingStatus.PendingConfirmation => "Chờ xác nhận",
            BookingStatus.Rejected => "Đã từ chối",
            BookingStatus.PendingPayment => "Chờ thanh toán",
            BookingStatus.Paid => "Đã thanh toán",
            BookingStatus.ReadyForPickup => "Sẵn sàng giao xe",
            BookingStatus.Rented => "Đang thuê",
            BookingStatus.PendingInspection => "Chờ kiểm tra xe",
            BookingStatus.Completed => "Đã hoàn tất",
            BookingStatus.Cancelled => "Đã hủy",
            BookingStatus.NoShow => "Khách không đến nhận xe",
            _ => status.ToString()
        },
        VehicleStatus status => status switch
        {
            VehicleStatus.Available => "Sẵn sàng",
            VehicleStatus.Rented => "Đang cho thuê",
            VehicleStatus.Inspection => "Đang kiểm tra",
            VehicleStatus.Maintenance => "Đang bảo trì",
            VehicleStatus.Inactive => "Ngừng hoạt động",
            _ => status.ToString()
        },
        PaymentStatus status => status switch
        {
            PaymentStatus.Pending => "Chờ thanh toán",
            PaymentStatus.Paid => "Đã thanh toán",
            PaymentStatus.Failed => "Thanh toán thất bại",
            PaymentStatus.Refunded => "Đã hoàn tiền",
            _ => status.ToString()
        },
        PaymentType type => type switch
        {
            PaymentType.Rental => "Tiền thuê xe",
            PaymentType.AdditionalCharge => "Phụ phí",
            PaymentType.Refund => "Hoàn tiền",
            PaymentType.Extension => "Tiền gia hạn",
            _ => type.ToString()
        },
        AdditionalChargeType type => type switch
        {
            AdditionalChargeType.LateReturn => "Trả xe muộn",
            AdditionalChargeType.Fuel => "Thiếu nhiên liệu",
            AdditionalChargeType.Cleaning => "Vệ sinh xe",
            AdditionalChargeType.ExcessMileage => "Vượt số km",
            AdditionalChargeType.Damage => "Hư hỏng",
            AdditionalChargeType.MissingAccessory => "Thiếu phụ kiện",
            AdditionalChargeType.Other => "Khác",
            _ => type.ToString()
        },
        BookingExtensionStatus status => status switch
        {
            BookingExtensionStatus.Pending => "Chờ duyệt",
            BookingExtensionStatus.Approved => "Đã duyệt",
            BookingExtensionStatus.Rejected => "Đã từ chối",
            BookingExtensionStatus.Paid => "Đã thanh toán",
            BookingExtensionStatus.Cancelled => "Đã hủy",
            _ => status.ToString()
        },
        MaintenanceStatus status => status switch
        {
            MaintenanceStatus.InProgress => "Đang thực hiện",
            MaintenanceStatus.Completed => "Đã hoàn tất",
            MaintenanceStatus.Cancelled => "Đã hủy",
            _ => status.ToString()
        },
        IncidentStatus status => status switch
        {
            IncidentStatus.Open => "Mới ghi nhận",
            IncidentStatus.Investigating => "Đang xử lý",
            IncidentStatus.Resolved => "Đã xử lý",
            IncidentStatus.Cancelled => "Đã hủy",
            _ => status.ToString()
        },
        IncidentType type => type switch
        {
            IncidentType.Accident => "Tai nạn",
            IncidentType.Damage => "Hư hỏng",
            IncidentType.Breakdown => "Hỏng xe",
            IncidentType.Theft => "Mất cắp",
            IncidentType.TrafficFine => "Vi phạm giao thông",
            IncidentType.Other => "Khác",
            _ => type.ToString()
        },
        DocumentStatus status => status switch
        {
            DocumentStatus.Pending => "Chờ xác minh",
            DocumentStatus.Verified => "Đã xác minh",
            DocumentStatus.Rejected => "Đã từ chối",
            _ => status.ToString()
        },
        VehicleDocumentType type => type switch
        {
            VehicleDocumentType.Registration => "Đăng ký xe",
            VehicleDocumentType.Inspection => "Đăng kiểm",
            VehicleDocumentType.Insurance => "Bảo hiểm",
            VehicleDocumentType.RoadFee => "Phí sử dụng đường bộ",
            VehicleDocumentType.Other => "Khác",
            _ => type.ToString()
        },
        PromotionType type => type switch
        {
            PromotionType.Percentage => "Giảm theo phần trăm",
            PromotionType.FixedAmount => "Giảm số tiền cố định",
            _ => type.ToString()
        },
        _ => value.ToString()
    };

    public static string ToVietnameseActor(this string? value) => value switch
    {
        "Admin" => "Quản trị viên",
        "Customer" => "Khách hàng",
        null or "" => "Không xác định",
        _ => value
    };

    public static string ToVietnameseAuditAction(this string? value) => value switch
    {
        "Create" => "Tạo mới",
        "Update" => "Cập nhật",
        "Delete" => "Xóa",
        "ChangeStatus" => "Đổi trạng thái",
        "Submit" => "Gửi xác minh",
        "Verify" => "Xác minh",
        "Reject" => "Từ chối",
        "Confirm" => "Xác nhận đơn",
        "MarkReady" => "Đánh dấu sẵn sàng",
        "Pay" => "Thanh toán",
        "CustomerCancel" => "Khách hàng hủy đơn",
        "AdminCancel" => "Quản trị viên hủy đơn",
        "MarkNoShow" => "Ghi nhận không đến nhận xe",
        "CreateHandover" => "Lập biên bản giao xe",
        "CreateReturn" => "Lập biên bản trả xe",
        "AddCharge" => "Thêm phụ phí",
        "RemoveCharge" => "Xóa phụ phí",
        "CompleteBooking" => "Hoàn tất đơn",
        "ApplyPromotion" => "Áp dụng khuyến mãi",
        "RemovePromotion" => "Gỡ khuyến mãi",
        "StartInvestigation" => "Bắt đầu xử lý",
        "Resolve" => "Hoàn tất xử lý",
        null or "" => "Không xác định",
        _ => value
    };

    public static string ToVietnameseEntity(this string? value) => value switch
    {
        "Booking" => "Đơn thuê",
        "Payment" => "Thanh toán",
        "Promotion" => "Khuyến mãi",
        "CustomerDocument" => "Giấy tờ khách hàng",
        "VehicleDocument" => "Giấy tờ xe",
        "VehicleHandover" => "Biên bản giao xe",
        "VehicleReturn" => "Biên bản trả xe",
        "AdditionalCharge" => "Phụ phí",
        "VehicleIncident" => "Sự cố xe",
        "Vehicle" => "Xe",
        "MaintenanceRecord" => "Phiếu bảo trì",
        null or "" => "Không xác định",
        _ => value
    };
}
