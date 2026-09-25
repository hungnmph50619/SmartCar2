using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Bookings;

public static class CounterRentalSchedule
{
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
