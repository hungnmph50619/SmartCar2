using SmartCar.Application.Features.Bookings;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingDateRulesTests
{
    [Fact]
    public void IsValidRange_RejectsPastPickup()
    {
        var pickup = DateTime.Now.AddHours(-1);
        var returnDate = DateTime.Now.AddDays(1);

        Assert.False(BookingDateRules.IsValidRange(pickup, returnDate));
    }

    [Fact]
    public void IsValidRange_RejectsReturnBeforePickup()
    {
        var pickup = DateTime.Now.AddDays(2);
        var returnDate = pickup.AddHours(-1);

        Assert.False(BookingDateRules.IsValidRange(pickup, returnDate));
    }

    [Fact]
    public void IsValidRange_AcceptsFutureRange()
    {
        var pickup = DateTime.Now.AddDays(1);
        var returnDate = pickup.AddDays(2);

        Assert.True(BookingDateRules.IsValidRange(pickup, returnDate));
    }

    [Fact]
    public void Overlaps_ReturnsTrueForIntersectingRanges()
    {
        var existingPickup = new DateTime(2026, 8, 10, 8, 0, 0);
        var existingReturn = new DateTime(2026, 8, 12, 8, 0, 0);

        Assert.True(BookingDateRules.Overlaps(
            new DateTime(2026, 8, 11, 8, 0, 0),
            new DateTime(2026, 8, 13, 8, 0, 0),
            existingPickup,
            existingReturn));
    }

    [Fact]
    public void Overlaps_AllowsBackToBackBookings()
    {
        var existingPickup = new DateTime(2026, 8, 10, 8, 0, 0);
        var existingReturn = new DateTime(2026, 8, 12, 8, 0, 0);

        Assert.False(BookingDateRules.Overlaps(
            existingReturn,
            existingReturn.AddDays(1),
            existingPickup,
            existingReturn));
    }

    [Fact]
    public void CounterRentalView_UsesConfiguredPickupLeadRule()
    {
        var repositoryRoot = FindRepositoryRoot();
        var viewPath = Path.Combine(
            repositoryRoot,
            "src",
            "SmartCar.Web",
            "Views",
            "Staff",
            "CounterRental.cshtml");
        var source = File.ReadAllText(viewPath);

        Assert.Contains("policy.MinimumPickupLeadMinutes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Date.now() + 4 * 60000", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IsValidRange_UsesConfiguredPickupLead()
    {
        var pickup = DateTime.Now.AddMinutes(20);
        var returnDate = pickup.AddDays(1);

        Assert.False(BookingDateRules.IsValidRange(pickup, returnDate, 30));
        Assert.True(BookingDateRules.IsValidRange(pickup, returnDate, 10));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Không tìm thấy thư mục gốc của repository SmartCar.");
    }
}
