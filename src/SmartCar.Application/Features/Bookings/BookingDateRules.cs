namespace SmartCar.Application.Features.Bookings;

public static class BookingDateRules
{
    public static bool IsValidRange(DateTime pickupDate, DateTime returnDate) =>
        pickupDate < returnDate;

    public static bool Overlaps(
        DateTime newPickup,
        DateTime newReturn,
        DateTime existingPickup,
        DateTime existingReturn) =>
        newPickup < existingReturn && newReturn > existingPickup;
}
