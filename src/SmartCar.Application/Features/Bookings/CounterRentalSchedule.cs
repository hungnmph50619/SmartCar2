using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Bookings;

public static class CounterRentalSchedule
{
    public static bool IsImmediate(
        BookingSource source,
        bool storedImmediate,
        DateTime createdAtUtc,
        DateTime pickupDate) =>
        source == BookingSource.StaffCounter &&
        (storedImmediate ||
         Math.Abs((pickupDate - DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc)
             .ToLocalTime()).TotalMinutes) <= 2);

    public static (DateTime PickupDate, DateTime ReturnDate) ForApproval(
        BookingSource source,
        bool storedImmediate,
        DateTime createdAtUtc,
        DateTime pickupDate,
        DateTime returnDate,
        DateTime now)
    {
        if (!IsImmediate(source, storedImmediate, createdAtUtc, pickupDate) ||
            pickupDate > now)
        {
            return (pickupDate, returnDate);
        }

        // The customer booked a duration at the counter, not a past departure time.
        // Move both ends together before the Admin commits the reservation.
        return (now, returnDate.Add(now - pickupDate));
    }

    public static DateTime CashHoldExpiresAtUtc(
        DateTime pickupLocal,
        RentalPolicySnapshot bookingPolicy) =>
        DateTime.SpecifyKind(pickupLocal, DateTimeKind.Local)
            .AddMinutes(bookingPolicy.NoShowGraceMinutes)
            .ToUniversalTime();

    public static DateTime LatestReturn(
        DateTime nextPickup,
        VehiclePickupMethod nextPickupMethod,
        RentalPolicySnapshot nextBookingPolicy) =>
        nextPickup.AddMinutes(-nextBookingPolicy.GetOperationalPreparationMinutes(nextPickupMethod));
}
