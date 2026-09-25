using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using Xunit;

namespace SmartCar.Tests;

public sealed class CounterRentalScheduleTests
{
    [Fact]
    public void LatestReturn_LeavesPreparationTimeBeforeNextCustomer()
    {
        var nextPickup = new DateTime(2026, 9, 26, 18, 0, 0);
        var policy = new RentalPolicySnapshot { VehicleTurnaroundMinutes = 60 };

        Assert.Equal(new DateTime(2026, 9, 26, 17, 0, 0),
            CounterRentalSchedule.LatestReturn(nextPickup, VehiclePickupMethod.StorePickup, policy));
    }

    [Fact]
    public void LatestReturn_AlsoLeavesDeliveryLeadWhenNextBookingIsDelivery()
    {
        var nextPickup = new DateTime(2026, 9, 26, 18, 0, 0);
        var policy = new RentalPolicySnapshot { VehicleTurnaroundMinutes = 60, DeliveryLeadMinutes = 30 };

        Assert.Equal(new DateTime(2026, 9, 26, 16, 30, 0),
            CounterRentalSchedule.LatestReturn(nextPickup, VehiclePickupMethod.Delivery, policy));
    }
}
