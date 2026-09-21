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

    [Fact]
    public void FundedRefundCode_IsRecognizedAsFunded()
    {
        var code = OverdueCompensationLedger.BuildFundedRefundCode(
            103,
            204,
            new DateTime(2026, 9, 20, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(OverdueCompensationLedger.IsFundedRefundCode(code));
        Assert.False(OverdueCompensationLedger.IsFundedRefundCode(null));
        Assert.False(OverdueCompensationLedger.IsFundedRefundCode(
            "OVERDUE-DEBT-103-204-20260920130000"));
    }


    [Fact]
    public void FundedRefundRelation_OnlyCountsExactSourcePair_AndKeepsLegacyConservative()
    {
        var exact = OverdueCompensationLedger.BuildFundedRefundCode(
            103,
            204,
            new DateTime(2026, 9, 20, 13, 0, 0, DateTimeKind.Utc));
        var otherSource = OverdueCompensationLedger.BuildFundedRefundCode(
            999,
            204,
            new DateTime(2026, 9, 20, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(
            OverdueCompensationLedger.TryParseFundedRefundRelation(
                exact,
                out var renterBookingId,
                out var affectedBookingId));
        Assert.Equal(103, renterBookingId);
        Assert.Equal(204, affectedBookingId);

        Assert.True(
            OverdueCompensationLedger.CountsTowardRefundRelation(
                exact,
                103,
                204));
        Assert.False(
            OverdueCompensationLedger.CountsTowardRefundRelation(
                otherSource,
                103,
                204));

        // Legacy compensation refunds did not carry OVERDUE-FUNDED markers.
        // Count them conservatively so historical rows cannot be paid twice.
        Assert.True(
            OverdueCompensationLedger.CountsTowardRefundRelation(
                null,
                103,
                204));
        Assert.True(
            OverdueCompensationLedger.CountsTowardRefundRelation(
                "OVERDUE-DEBT-103-204-20260920130000",
                103,
                204));
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
