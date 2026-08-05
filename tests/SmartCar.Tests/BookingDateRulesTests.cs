using SmartCar.Application.Features.Bookings;

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
}
