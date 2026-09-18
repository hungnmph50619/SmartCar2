namespace SmartCar.Application.Features.Bookings;

public static class BookingDateRules
{
    public const int MinimumPickupLeadMinutes = 5;

    public static bool IsValidRange(DateTime pickupDate, DateTime returnDate)
    {
        var minimumPickupTime = DateTime.Now.AddMinutes(MinimumPickupLeadMinutes);
        return pickupDate >= minimumPickupTime && pickupDate < returnDate;
    }

    public static bool Overlaps(
        DateTime newPickup,
        DateTime newReturn,
        DateTime existingPickup,
        DateTime existingReturn) =>
        newPickup < existingReturn && newReturn > existingPickup;
}
