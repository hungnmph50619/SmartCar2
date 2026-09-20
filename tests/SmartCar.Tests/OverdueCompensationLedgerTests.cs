using SmartCar.Domain.Constants;
using Xunit;

namespace SmartCar.Tests;

public sealed class OverdueCompensationLedgerTests
{
    [Theory]
    [InlineData("OVERDUE-DEBT-103-204-20260920120000", 103, 204)]
    [InlineData("OVERDUE-DEBT-1-999-20260920120000", 1, 999)]
    public void TryParseDebtRelation_ReturnsSourceAndAffectedBooking(
        string code,
        int expectedRenterBookingId,
        int expectedAffectedBookingId)
    {
        var parsed = OverdueCompensationLedger.TryParseDebtRelation(
            code,
            out var renterBookingId,
            out var affectedBookingId);

        Assert.True(parsed);
        Assert.Equal(expectedRenterBookingId, renterBookingId);
        Assert.Equal(expectedAffectedBookingId, affectedBookingId);
    }

    [Theory]
    [InlineData(700000, 300000, 700000, 300000)]
    [InlineData(700000, 300000, 1000000, 0)]
    [InlineData(0, 300000, 0, 300000)]
    [InlineData(700000, 0, 0, 700000)]
    [InlineData(700000, 300000, 1200000, 0)]
    public void CalculateRefundReleaseAmount_ReleasesOnlyNewlyFundedAmount(
        decimal fundedFromDeposit,
        decimal fundedFromDebt,
        decimal alreadyRecorded,
        decimal expected)
    {
        Assert.Equal(
            expected,
            OverdueCompensationLedger.CalculateRefundReleaseAmount(
                fundedFromDeposit,
                fundedFromDebt,
                alreadyRecorded));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OVERDUE-COMP-103-204-20260920120000")]
    [InlineData("OVERDUE-DEBT-x-204-20260920120000")]
    [InlineData("OVERDUE-DEBT-103-x-20260920120000")]
    public void TryParseDebtRelation_RejectsInvalidCodes(string? code)
    {
        Assert.False(
            OverdueCompensationLedger.TryParseDebtRelation(
                code,
                out _,
                out _));
    }
}
