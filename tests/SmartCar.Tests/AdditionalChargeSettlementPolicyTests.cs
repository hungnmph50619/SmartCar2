using SmartCar.Domain.Constants;
using Xunit;

namespace SmartCar.Tests;

public sealed class AdditionalChargeSettlementPolicyTests
{
    [Fact]
    public void CreateReadyMarker_CreatesRecognizableMarker()
    {
        var marker = AdditionalChargeSettlementPolicy.CreateReadyMarker(
            39,
            new DateTime(2026, 9, 20, 6, 15, 0, DateTimeKind.Utc));

        Assert.StartsWith("ADD-READY-39-", marker, StringComparison.Ordinal);
        Assert.True(AdditionalChargeSettlementPolicy.IsReadyMarker(marker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("QR2026092006150039")]
    [InlineData("CASH-ADD-39-20260920061500")]
    public void IsReadyMarker_RejectsNonFinalizationCodes(string? value)
    {
        Assert.False(AdditionalChargeSettlementPolicy.IsReadyMarker(value));
    }
}
