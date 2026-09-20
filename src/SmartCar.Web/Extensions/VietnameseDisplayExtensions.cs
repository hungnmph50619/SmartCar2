using System.Text.RegularExpressions;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.Extensions;

public static class VietnameseDisplayExtensions
{
    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

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
            BookingStatus.AwaitingRefund => "Chờ hoàn tiền",
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
            PaymentStatus.AwaitingConfirmation => "Chờ xác nhận chuyển khoản",
            PaymentStatus.AwaitingRefund => "Chờ chủ/admin duyệt hoàn",
            PaymentStatus.RefundApproved => "Đã duyệt - chờ nhân viên hoàn",
            _ => status.ToString()
        },
        PaymentType type => type switch
        {
            PaymentType.Deposit => "Tiền cọc",
            PaymentType.Rental => "Tiền thuê xe",
            PaymentType.AdditionalCharge => "Phụ phí",
            PaymentType.Refund => "Hoàn tiền",
            PaymentType.Extension => "Tiền gia hạn",
            PaymentType.VehicleSwapAdjustment => "Chênh lệch đổi xe",
            _ => type.ToString()
        },
        VehiclePickupMethod method => method switch
        {
            VehiclePickupMethod.StorePickup => "Nhận tại cửa hàng",
            VehiclePickupMethod.Delivery => "Giao xe tận nơi",
            _ => method.ToString()
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
            BookingExtensionStatus.NeedsEvidence => "Cần bổ sung minh chứng",
            BookingExtensionStatus.Approved => "Đã duyệt - chờ thanh toán",
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
            DocumentStatus.Rejected => "Cần gửi lại",
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
        "Submit" => "Gửi giấy tờ xác minh",
        "SubmitKycPackage" => "Gửi hồ sơ xác minh",
        "Verify" => "Xác minh giấy tờ",
        "VerifyAll" => "Duyệt hồ sơ xác minh",
        "Reject" => "Từ chối",
        "RequestResubmission" => "Yêu cầu cập nhật giấy tờ",
        "RequestUpdate" => "Yêu cầu cập nhật giấy tờ",
        "SendDocumentReminder" => "Nhắc hoàn thiện giấy tờ",
        "LockCustomer" => "Khóa tài khoản khách hàng",
        "UnlockCustomer" => "Mở khóa tài khoản khách hàng",
        "Confirm" => "Xác nhận đơn",
        "MarkReady" => "Đánh dấu sẵn sàng",
        "Pay" => "Thanh toán",
        "ConfirmQrPayment" => "Xác nhận thanh toán",
        "RejectQrPayment" => "Yêu cầu gửi lại xác nhận thanh toán",
        "Refund" => "Hoàn tiền",
        "RefundBatch" => "Hoàn tiền theo đơn",
        "RepairForceMajeureCompensation" => "Điều chỉnh quyết toán bất khả kháng cũ",
        "CustomerCancel" => "Khách hàng hủy đơn",
        "AdminCancel" => "Quản trị viên hủy đơn",
        "MarkNoShow" => "Ghi nhận không đến nhận xe",
        "CreateHandover" => "Lập biên bản giao xe",
        "CreateReturn" => "Lập biên bản trả xe",
        "AddCharge" => "Thêm phụ phí",
        "RemoveCharge" => "Xóa phụ phí",
        "CompleteBooking" => "Hoàn tất đơn",
        "StartInvestigation" => "Bắt đầu xử lý",
        "Resolve" => "Hoàn tất xử lý",
        "UpdateAvatar" => "Đổi ảnh đại diện",
        "ChangePassword" => "Đổi mật khẩu",
        "UpdateBankAccount" => "Cập nhật tài khoản ngân hàng",
        "ViewKycDocumentImage" => "Xem ảnh giấy tờ xác minh",
        "CreateExtensionForCustomer" => "Ghi nhận gia hạn qua điện thoại",
        "ResolveExtensionConflictByVehicleSwap" => "Xử lý xung đột gia hạn bằng đổi xe",
        "ResolveExtensionConflictByCancellation" => "Xử lý xung đột gia hạn bằng hủy đơn kế tiếp",
        "UploadHandoverSigned" => "Tải bản giao xe đã ký",
        "UploadReturnSigned" => "Tải bản trả xe đã ký",
        "UpdateRentalPolicy" => "Cập nhật chính sách",
        null or "" => "Không xác định",
        _ => value
    };

    public static string ToVietnameseEntity(this string? value) => value switch
    {
        "Booking" => "Đơn thuê",
        "BookingExtension" => "Yêu cầu gia hạn",
        "Payment" => "Thanh toán",
        "CustomerDocument" => "Giấy tờ khách hàng",
        "VehicleDocument" => "Giấy tờ xe",
        "VehicleHandover" => "Biên bản giao xe",
        "VehicleReturn" => "Biên bản trả xe",
        "AdditionalCharge" => "Phụ phí",
        "VehicleIncident" => "Sự cố xe",
        "Vehicle" => "Xe",
        "MaintenanceRecord" => "Phiếu bảo trì",
        "BusinessSetting" => "Cấu hình nghiệp vụ",
        "UserProfile" => "Hồ sơ người dùng",
        "UserAccount" => "Tài khoản người dùng",
        null or "" => "Không xác định",
        _ => value
    };

    public static string ToFriendlyAuditDescription(this string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Không có mô tả.";
        }

        var withoutGuids = GuidPattern.Replace(value, string.Empty);
        var normalized = Regex.Replace(withoutGuids, @"\s+", " ").Trim();
        normalized = normalized
            .Replace("khách hàng .", "khách hàng.", StringComparison.OrdinalIgnoreCase)
            .Replace("khách hàng ,", "khách hàng,", StringComparison.OrdinalIgnoreCase)
            .Replace("Gửi thông tin GPLX cùng ảnh mặt trước và mặt sau để xác minh.", "Khách hàng đã gửi GPLX cùng ảnh mặt trước và mặt sau để xác minh.", StringComparison.OrdinalIgnoreCase)
            .Replace("Gửi thông tin CCCD cùng ảnh mặt trước và mặt sau để xác minh.", "Khách hàng đã gửi CCCD cùng ảnh mặt trước và mặt sau để xác minh.", StringComparison.OrdinalIgnoreCase)
            .Replace("Xác minh GPLX mặt sau của khách hàng.", "Quản trị viên đã xác minh ảnh mặt sau GPLX của khách hàng.", StringComparison.OrdinalIgnoreCase)
            .Replace("Xác minh GPLX của khách hàng.", "Quản trị viên đã xác minh GPLX của khách hàng.", StringComparison.OrdinalIgnoreCase)
            .Replace("Xác minh CCCD mặt sau của khách hàng.", "Quản trị viên đã xác minh ảnh mặt sau CCCD của khách hàng.", StringComparison.OrdinalIgnoreCase)
            .Replace("Xác minh CCCD của khách hàng.", "Quản trị viên đã xác minh CCCD của khách hàng.", StringComparison.OrdinalIgnoreCase);

        return normalized;
    }
}
