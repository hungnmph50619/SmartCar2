namespace SmartCar.Domain.Enums;

public enum PaymentStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Refunded = 4,
    AwaitingConfirmation = 5,
    AwaitingRefund = 6,
    RefundApproved = 7
}