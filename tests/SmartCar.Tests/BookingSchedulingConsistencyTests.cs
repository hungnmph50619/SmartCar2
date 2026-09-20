using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingSchedulingConsistencyTests
{
    [Fact]
    public void HasOperationalConflict_StorePickupRequiresFullTurnaroundGap()
    {
        var firstPickup = new DateTime(2026, 9, 18, 8, 0, 0);
        var firstReturn = new DateTime(2026, 9, 18, 10, 0, 0);
        var secondReturn = new DateTime(2026, 9, 18, 14, 0, 0);

        Assert.True(RentalPolicy.HasOperationalConflict(
            firstPickup,
            firstReturn,
            VehiclePickupMethod.StorePickup,
            firstReturn.AddMinutes(RentalPolicy.VehicleTurnaroundMinutes - 1),
            secondReturn,
            VehiclePickupMethod.StorePickup));

        Assert.False(RentalPolicy.HasOperationalConflict(
            firstPickup,
            firstReturn,
            VehiclePickupMethod.StorePickup,
            firstReturn.AddMinutes(RentalPolicy.VehicleTurnaroundMinutes),
            secondReturn,
            VehiclePickupMethod.StorePickup));
    }

    [Fact]
    public void HasOperationalConflict_DeliveryUsesSameTurnaroundGap()
    {
        var firstPickup = new DateTime(2026, 9, 18, 8, 0, 0);
        var firstReturn = new DateTime(2026, 9, 18, 10, 0, 0);
        var requiredGap = RentalPolicy.VehicleTurnaroundMinutes;

        Assert.True(RentalPolicy.HasOperationalConflict(
            firstPickup,
            firstReturn,
            VehiclePickupMethod.StorePickup,
            firstReturn.AddMinutes(requiredGap - 1),
            firstReturn.AddHours(4),
            VehiclePickupMethod.Delivery));

        Assert.False(RentalPolicy.HasOperationalConflict(
            firstPickup,
            firstReturn,
            VehiclePickupMethod.StorePickup,
            firstReturn.AddMinutes(requiredGap),
            firstReturn.AddHours(4),
            VehiclePickupMethod.Delivery));
    }

    [Fact]
    public void HasOperationalConflict_IsSymmetricForBookingsInEitherOrder()
    {
        var bookingAStart = new DateTime(2026, 9, 18, 12, 0, 0);
        var bookingAEnd = new DateTime(2026, 9, 18, 18, 0, 0);
        var bookingBStart = new DateTime(2026, 9, 18, 10, 31, 0);
        var bookingBEnd = new DateTime(2026, 9, 18, 11, 0, 0);

        var ab = RentalPolicy.HasOperationalConflict(
            bookingAStart,
            bookingAEnd,
            VehiclePickupMethod.Delivery,
            bookingBStart,
            bookingBEnd,
            VehiclePickupMethod.StorePickup);

        var ba = RentalPolicy.HasOperationalConflict(
            bookingBStart,
            bookingBEnd,
            VehiclePickupMethod.StorePickup,
            bookingAStart,
            bookingAEnd,
            VehiclePickupMethod.Delivery);

        Assert.True(ab);
        Assert.Equal(ab, ba);
    }
}
