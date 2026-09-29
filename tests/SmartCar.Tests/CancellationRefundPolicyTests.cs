using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using Xunit;

namespace SmartCar.Tests;

public sealed class CancellationRefundPolicyTests
{
    [Fact]
    public void FreeCancellationWindow_TreatsPersistedUnspecifiedPaidAtAsUtc()
    {
        var paidAtUtc = new DateTime(2026, 9, 29, 15, 40, 0, DateTimeKind.Utc);
        var persistedPaidAt = DateTime.SpecifyKind(paidAtUtc, DateTimeKind.Unspecified);
        var cancelledAtUtc = paidAtUtc.AddMinutes(1);

        Assert.True(CancellationRefundPolicy.IsWithinFreeCancellationWindowUtc(
            cancelledAtUtc,
            persistedPaidAt,
            60));
    }

    [Fact]
    public void FreeCancellationWindow_RejectsCancellationAfterConfiguredWindow()
    {
        var paidAtUtc = new DateTime(2026, 9, 29, 15, 40, 0, DateTimeKind.Utc);
        var persistedPaidAt = DateTime.SpecifyKind(paidAtUtc, DateTimeKind.Unspecified);
        var cancelledAtUtc = paidAtUtc.AddMinutes(61);

        Assert.False(CancellationRefundPolicy.IsWithinFreeCancellationWindowUtc(
            cancelledAtUtc,
            persistedPaidAt,
            60));
    }

    [Fact]
    public void GetRentalRefundRate_RefundsAllWithinFirstHourAfterPayment()
    {
        var paidAt = new DateTime(2026, 9, 13, 10, 0, 0);
        var cancelledAt = paidAt.AddMinutes(30);
        var pickupDate = cancelledAt.AddHours(48);

        var rate = CancellationRefundPolicy.GetRentalRefundRate(cancelledAt, pickupDate, paidAt);

        Assert.Equal(1.00m, rate);
    }

    [Fact]
    public void GetRentalRefundRate_FreeWindowStillAppliesWhenPickupIsLessThan24HoursAway()
    {
        var paidAt = new DateTime(2026, 9, 13, 10, 0, 0);
        var cancelledAt = paidAt.AddMinutes(30);
        var pickupDate = cancelledAt.AddHours(12);

        var rate = CancellationRefundPolicy.GetRentalRefundRate(cancelledAt, pickupDate, paidAt);

        Assert.Equal(1.00m, rate);
    }

    [Fact]
    public void GetRentalRefundRate_NewBookingSnapshotRefundsAllWithinFreeWindowEvenNearPickup()
    {
        var paidAt = new DateTime(2026, 9, 20, 20, 0, 0);
        var cancelledAt = paidAt.AddMinutes(10);
        var pickupDate = new DateTime(2026, 9, 20, 21, 0, 0);
        var policy = new RentalPolicySnapshot
        {
            FreeCancellationWindowMinutes = 60
        };

        var rate = CancellationRefundPolicy.GetRentalRefundRate(
            cancelledAt,
            pickupDate,
            paidAt,
            policy);

        Assert.Equal(1.00m, rate);
    }

    [Fact]
    public void GetRentalRefundRate_LegacySnapshotFieldsDoNotRestoreOld24HourRequirement()
    {
        var paidAt = new DateTime(2026, 9, 20, 20, 0, 0);
        var cancelledAt = paidAt.AddMinutes(10);
        var pickupDate = new DateTime(2026, 9, 20, 21, 0, 0);
        var policy = RentalPolicySnapshot.FromJson(
            """{"FreeCancellationWindowMinutes":60,"MinimumHoursForFreeCancellation":24,"FreeCancellationRequiresMinimumLead":true}""");

        var rate = CancellationRefundPolicy.GetRentalRefundRate(
            cancelledAt,
            pickupDate,
            paidAt,
            policy);

        Assert.Equal(1.00m, rate);
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
    public void GetRentalRefundRate_UsesBookingSnapshotConfiguration()
    {
        var cancelledAt = new DateTime(2026, 9, 13, 10, 0, 0);
        var pickupDate = cancelledAt.AddHours(30);
        var policy = new RentalPolicySnapshot
        {
            FreeCancellationWindowMinutes = 15,
            CancellationTier1Hours = 120,
            CancellationTier1RefundPercent = 80,
            CancellationTier2Hours = 36,
            CancellationTier2RefundPercent = 60,
            CancellationTier3Hours = 18,
            CancellationTier3RefundPercent = 40,
            CancellationTier4Hours = 3,
            CancellationTier4RefundPercent = 10,
            CancellationBelowTierRefundPercent = 0
        };

        var rate = CancellationRefundPolicy.GetRentalRefundRate(
            cancelledAt,
            pickupDate,
            rentalPaidAt: null,
            policy);

        Assert.Equal(0.40m, rate);
    }

    [Fact]
    public void RentalPolicy_UsesThirtyMinutePaymentWindowAndSixtyMinuteTurnaround()
    {
        Assert.Equal(30, RentalPolicy.BookingPaymentHoldMinutes);
        Assert.Equal(60, RentalPolicy.VehicleTurnaroundMinutes);
        Assert.Equal(1.00m, RentalPolicy.NoShowFeeRate);
    }
}
