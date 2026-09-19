using System.ComponentModel.DataAnnotations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using Xunit;

namespace SmartCar.Tests;

public sealed class RentalPolicySnapshotTests
{
    [Theory]
    [InlineData(300, 2250000)]
    [InlineData(200, 1500000)]
    [InlineData(150, 1125000)]
    public void DepositUsesConfiguredPercent(int percent, int expected)
    {
        var policy = new RentalPolicySnapshot { DepositPercent = percent };
        Assert.Equal((decimal)expected, policy.CalculateDeposit(750000m));
    }

    [Theory]
    [InlineData(2.8, 30000)]
    [InlineData(3, 30000)]
    [InlineData(3.01, 40000)]
    [InlineData(4, 40000)]
    [InlineData(4.01, 50000)]
    public void DeliveryChargesEachStartedExtraKilometer(double distance, int expected)
        => Assert.Equal((decimal)expected, new RentalPolicySnapshot().DeliveryFeeForDistance(distance));

    [Fact]
    public void FreeDeliveryAndStorePickupAreSupported()
    {
        var policy = new RentalPolicySnapshot { BaseDeliveryFee = 0m, DeliveryFeePerExtraKm = 0m };
        Assert.Equal(0m, policy.DeliveryFeeForDistance(10));
        Assert.Equal(0m, policy.CalculateDeliveryFee(VehiclePickupMethod.StorePickup, 21m, 105m));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(1470, 1)]
    [InlineData(1471, 2)]
    public void LateFeeStartsAfterGracePeriod(int minutes, int days)
        => Assert.Equal(days, new RentalPolicySnapshot { LateReturnGraceMinutes = 30 }.LateChargeDays(minutes));

    [Fact]
    public void EditingSettingsDoesNotChangeExistingBookingOrTerms()
    {
        var configured = new RentalPolicySnapshot { DepositPercent = 200, IncludedKilometersPerDay = 250,
            DepositHoldDays = 7, TrafficFineTerms = "Điều khoản đã chốt" };
        var booking = new Booking { PolicyJson = configured.ToJson(), DepositHoldDaysApplied = configured.DepositHoldDays };
        configured.DepositPercent = 300;
        configured.IncludedKilometersPerDay = 400;
        configured.DepositHoldDays = 15;
        configured.TrafficFineTerms = "Điều khoản mới";
        Assert.Equal(1500000m, booking.Policy.CalculateDeposit(750000));
        Assert.Equal(250, booking.Policy.IncludedKilometersPerDay);
        Assert.Equal(7, booking.DepositHoldDaysApplied);
        Assert.Equal("Điều khoản đã chốt", booking.Policy.TrafficFineTerms);
        Assert.Equal(750, 3 * booking.Policy.IncludedKilometersPerDay); // extension uses original daily allowance
    }

    [Fact]
    public void LegacyBookingRetainsLegacyPolicy()
    {
        var booking = new Booking();
        Assert.Equal(300m, booking.Policy.DepositPercent);
        Assert.Equal(300, booking.Policy.IncludedKilometersPerDay);
        Assert.Equal(5000m, booking.Policy.ExcessKilometerFee);
        Assert.Equal(1, booking.Policy.LateChargeDays(1));
        Assert.Equal(RentalPolicy.DamageCompensationTerms, booking.Policy.DamageCompensationTerms);
    }

    [Fact]
    public void InvalidConfigurationIsRejected()
    {
        static bool Valid(RentalPolicySnapshot p) => Validator.TryValidateObject(p, new ValidationContext(p), new List<ValidationResult>(), true);
        Assert.True(Valid(new RentalPolicySnapshot()));
        Assert.False(Valid(new RentalPolicySnapshot { DepositPercent = -1 }));
        Assert.False(Valid(new RentalPolicySnapshot { DepositPercent = 0.5m }));
        Assert.False(Valid(new RentalPolicySnapshot { IncludedKilometersPerDay = 0 }));
        Assert.False(Valid(new RentalPolicySnapshot { IncludedDeliveryDistanceKm = 51, MaxDeliveryDistanceKm = 50 }));
        Assert.False(Valid(new RentalPolicySnapshot { BaseDeliveryFee = 0.1m }));
        Assert.False(Valid(new RentalPolicySnapshot { TrafficFineTerms = " " }));
        Assert.False(Valid(new RentalPolicySnapshot { DamageCompensationTerms = new string('x', 1501) }));
    }
}
