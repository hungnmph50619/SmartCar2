using SmartCar.Application.Features.Operations;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingHoldAbusePolicyTests
{
    [Fact]
    public void FirstTimeout_CoolsSameVehicleForThirtyMinutes()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var timeout = now.AddMinutes(-5);

        var result = BookingHoldAbusePolicy.Evaluate(now, new[] { timeout }, new[] { timeout });

        Assert.Equal(1, result.TimeoutCount24Hours);
        Assert.Equal(timeout.AddMinutes(30), result.SameVehicleCooldownUntil);
        Assert.Null(result.AccountBlockedUntil);
    }

    [Fact]
    public void SecondSameVehicleTimeout_CoolsForTwoHours()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var first = now.AddHours(-3);
        var second = now.AddMinutes(-10);

        var result = BookingHoldAbusePolicy.Evaluate(now, new[] { first, second }, new[] { first, second });

        Assert.Equal(second.AddMinutes(120), result.SameVehicleCooldownUntil);
        Assert.Null(result.AccountBlockedUntil);
    }

    [Fact]
    public void ThirdTimeoutAcrossVehicles_BlocksAccountForTwentyFourHours()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var events = new[] { now.AddHours(-5), now.AddHours(-2), now.AddMinutes(-15) };

        var result = BookingHoldAbusePolicy.Evaluate(now, events, Array.Empty<DateTime>());

        Assert.Equal(3, result.TimeoutCount24Hours);
        Assert.Equal(events[^1].AddHours(24), result.AccountBlockedUntil);
    }

    [Fact]
    public void OldTimeouts_OutsideRollingWindow_DoNotCount()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var old = now.AddHours(-25);

        var result = BookingHoldAbusePolicy.Evaluate(now, new[] { old }, new[] { old });

        Assert.Equal(0, result.TimeoutCount24Hours);
        Assert.Null(result.SameVehicleCooldownUntil);
        Assert.Null(result.AccountBlockedUntil);
    }
}
