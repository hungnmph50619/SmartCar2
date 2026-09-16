using SmartCar.Application.Features.Reports;
using Xunit;

namespace SmartCar.Tests;

public sealed class ReportDateRangeTests
{
    private static readonly DateTime Today = new(2026, 9, 16);

    [Theory]
    [InlineData("2026-10-08", "2026-11-06")]
    [InlineData("2026-09-01", "2026-09-17")]
    [InlineData("2026-09-16", "2026-09-15")]
    [InlineData("2026-02-30", "2026-09-16")]
    [InlineData("invalid", "2026-09-16")]
    [InlineData("", "")]
    [InlineData(null, "2026-09-16")]
    [InlineData("2026-09-01", null)]
    [InlineData("0001-01-01", "2026-09-16")]
    [InlineData("9999-12-31", "9999-12-31")]
    public void RejectsInvalidRanges(string? from, string? to)
    {
        Assert.False(ReportDateRange.TryCreate(from, to, Today, out var range, out var error));
        Assert.Null(range);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("2026-09-16", "2026-09-16")]
    [InlineData("2024-02-29", "2024-03-01")]
    [InlineData("2026-09-01", "2026-09-16")]
    public void AcceptsValidRanges(string from, string to)
    {
        Assert.True(ReportDateRange.TryCreate(from, to, Today, out var range, out var error));
        Assert.NotNull(range);
        Assert.Null(error);
    }

    [Fact]
    public void InitialVisitDefaultsToThirtyDaysEndingToday()
    {
        Assert.True(ReportDateRange.TryCreate(null, null, Today, out var range, out _));
        Assert.Equal(Today.AddDays(-29), range!.From);
        Assert.Equal(Today, range.To);
    }
}
