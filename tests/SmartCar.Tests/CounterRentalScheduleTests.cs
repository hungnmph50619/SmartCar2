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

    [Fact]
    public void CashHold_EndsAfterPickupAndNoShowWindow()
    {
        var pickupLocal = DateTime.Now.Date.AddDays(1).AddHours(10);
        var policy = new RentalPolicySnapshot { NoShowGraceMinutes = 30 };

        Assert.Equal(pickupLocal.AddMinutes(30).ToUniversalTime(),
            CounterRentalSchedule.CashHoldExpiresAtUtc(pickupLocal, policy));
    }

    [Fact]
    public void ImmediateCounterApproval_PreservesBookedDurationAfterReviewDelay()
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-12);
        var pickup = createdAt.ToLocalTime();
        var returnDate = pickup.AddDays(1);
        var approval = pickup.AddMinutes(12);

        var adjusted = CounterRentalSchedule.ForApproval(
            BookingSource.StaffCounter, true, createdAt, pickup, returnDate, approval);

        Assert.Equal(approval, adjusted.PickupDate);
        Assert.Equal(returnDate.AddMinutes(12), adjusted.ReturnDate);
    }

    [Fact]
    public void ScheduledCounterApproval_DoesNotMovePickupOrReturn()
    {
        var pickup = DateTime.Now.AddHours(2);
        var returnDate = pickup.AddDays(1);

        var adjusted = CounterRentalSchedule.ForApproval(
            BookingSource.StaffCounter, false, DateTime.UtcNow, pickup, returnDate, pickup.AddMinutes(1));

        Assert.Equal(pickup, adjusted.PickupDate);
        Assert.Equal(returnDate, adjusted.ReturnDate);
    }

    [Fact]
    public void LegacyImmediateCounterApproval_RecognizesPickupNearCreation()
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-10);
        var pickup = createdAt.ToLocalTime();

        Assert.True(CounterRentalSchedule.IsImmediate(
            BookingSource.StaffCounter, false, createdAt, pickup));
        Assert.False(CounterRentalSchedule.IsImmediate(
            BookingSource.CustomerWeb, false, createdAt, pickup));
    }
}
