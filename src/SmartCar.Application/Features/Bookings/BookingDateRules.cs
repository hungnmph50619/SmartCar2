namespace SmartCar.Application.Features.Bookings;

public static class BookingDateRules
{
    public static bool IsValidRange(DateTime pickupDate, DateTime returnDate)
    {
        var minimumPickupTime = DateTime.Now.AddMinutes(5);
        return pickupDate >= minimumPickupTime && pickupDate < returnDate;
    }

    public static bool Overlaps(
        DateTime newPickup,
        DateTime newReturn,
        DateTime existingPickup,
        DateTime existingReturn) =>
        newPickup < existingReturn && newReturn > existingPickup;
}
