using SmartCar.Application.Features.Reports;
using Xunit;

namespace SmartCar.Tests;

public sealed class ReportFinancialRulesTests
{
    [Fact]
    public void IncidentOperatingCost_ExcludesTrafficFineChargedToCustomer()
    {
        var actualCost = 100_000m;
        var fineAmount = 10_000_000m;

        var operatingCost = ReportFinancialRules.IncidentOperatingCost(
            actualCost,
            fineAmount);

        Assert.Equal(100_000m, operatingCost);
    }

    [Fact]
    public void IncidentOperatingCost_IsZeroWhenIncidentOnlyHasTrafficFine()
    {
        var operatingCost = ReportFinancialRules.IncidentOperatingCost(
            actualCost: 0m,
            fineAmount: 10_000_000m);

        Assert.Equal(0m, operatingCost);
    }
}
