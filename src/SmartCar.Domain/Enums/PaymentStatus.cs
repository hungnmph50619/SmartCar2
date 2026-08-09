namespace SmartCar.Domain.Enums;

public enum PaymentStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Refunded = 4,

    // Khách báo đã chuyển QR, chờ admin kiểm tra.
    AwaitingConfirmation = 5,

    // Hệ thống đã tạo khoản hoàn, chờ admin chuyển tiền.
    AwaitingRefund = 6
}