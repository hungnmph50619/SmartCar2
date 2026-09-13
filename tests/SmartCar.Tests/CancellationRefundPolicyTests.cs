using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using Xunit;

namespace SmartCar.Tests;

public sealed class CancellationRefundPolicyTests
{
    [Fact]
    public void GetRentalRefundRate_RefundsAllWithinFirstHourWhenTripIsAtLeast24HoursAway()
    {
        var paidAt = new DateTime(2026, 9, 13, 10, 0, 0);
        var cancelledAt = paidAt.AddMinutes(30);
        var pickupDate = cancelledAt.AddHours(48);

        var rate = CancellationRefundPolicy.GetRentalRefundRate(cancelledAt, pickupDate, paidAt);

        Assert.Equal(1.00m, rate);
    }

    [Fact]
    public void GetRentalRefundRate_DoesNotApplyFreeWindowWhenTripIsLessThan24HoursAway()
    {
        var paidAt = new DateTime(2026, 9, 13, 10, 0, 0);
        var cancelledAt = paidAt.AddMinutes(30);
        var pickupDate = cancelledAt.AddHours(12);

        var rate = CancellationRefundPolicy.GetRentalRefundRate(cancelledAt, pickupDate, paidAt);

        Assert.Equal(0.20m, rate);
    }

    [Theory]
    [InlineData(192, 0.90)]
    [InlineData(168, 0.90)]
    [InlineData(72, 0.70)]
    [InlineData(48, 0.70)]
    [InlineData(30, 0.50)]
    [InlineData(24, 0.50)]
    [InlineData(12, 0.20)]
    [InlineData(6, 0.20)]
    [InlineData(5, 0.00)]
    public void GetRentalRefundRate_AppliesConfiguredTimeTiers(double hoursBeforePickup, decimal expectedRate)
    {
        var cancelledAt = new DateTime(2026, 9, 13, 10, 0, 0);
        var pickupDate = cancelledAt.AddHours(hoursBeforePickup);

        var rate = CancellationRefundPolicy.GetRentalRefundRate(cancelledAt, pickupDate, rentalPaidAt: null);

        Assert.Equal(expectedRate, rate);
    }

    [Fact]
    public void RentalPolicy_UsesThirtyMinutePaymentWindowAndSixtyMinuteTurnaround()
    {
        Assert.Equal(30, RentalPolicy.BookingPaymentHoldMinutes);
        Assert.Equal(60, RentalPolicy.VehicleTurnaroundMinutes);
        Assert.Equal(1.00m, RentalPolicy.NoShowFeeRate);
    }
}
