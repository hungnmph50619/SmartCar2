namespace SmartCar.Domain.Enums;

public enum BookingStatus
{
    PendingConfirmation = 1,
    Rejected = 2,
    PendingPayment = 3,
    Paid = 4,
    ReadyForPickup = 5,
    Rented = 6,
    PendingInspection = 7,
    Completed = 8,
    Cancelled = 9,
    NoShow = 10,
    AwaitingRefund = 11
}
