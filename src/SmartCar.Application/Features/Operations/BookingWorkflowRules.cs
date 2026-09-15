using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Operations;

public static class BookingWorkflowRules
{
    private static readonly BookingStatus[] CancellableBeforeHandoverStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup
    };

    private static readonly BookingStatus[] ActiveStaffStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection,
        BookingStatus.AwaitingRefund
    };

    public static bool CanCancelBeforeHandover(
        BookingStatus status,
        bool hasHandover) =>
        !hasHandover && CancellableBeforeHandoverStatuses.Contains(status);

    public static bool IsStaffWorkItem(
        BookingStatus status,
        bool hasOpenRefund)
    {
        if (ActiveStaffStatuses.Contains(status))
        {
            return true;
        }

        return hasOpenRefund &&
               status is BookingStatus.Cancelled or BookingStatus.NoShow;
    }
}
