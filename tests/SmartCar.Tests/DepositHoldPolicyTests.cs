using SmartCar.Domain.Constants;
using Xunit;

namespace SmartCar.Tests;

public sealed class DepositHoldPolicyTests
{
    [Fact]
    public void CalculateEligibleAt_AddsConfiguredDaysToActualReturnTime()
    {
        var returnedAt = new DateTime(2026, 9, 10, 10, 30, 0);

        var eligibleAt = DepositHoldPolicy.CalculateEligibleAt(returnedAt, 15);

        Assert.Equal(new DateTime(2026, 9, 25, 10, 30, 0), eligibleAt);
    }

    [Fact]
    public void ZeroDays_MakesDepositEligibleAtReturnTime()
    {
        var returnedAt = new DateTime(2026, 9, 10, 10, 30, 0);

        var eligibleAt = DepositHoldPolicy.CalculateEligibleAt(returnedAt, 0);

        Assert.Equal(returnedAt, eligibleAt);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(15, 15)]
    [InlineData(999, DepositHoldPolicy.MaxDays)]
    public void NormalizeDays_ClampsToSupportedRange(int input, int expected)
    {
        Assert.Equal(expected, DepositHoldPolicy.NormalizeDays(input));
    }
}
