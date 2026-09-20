namespace SmartCar.Application.Features.Bookings;

public static class BookingDateRules
{
    public const int MinimumPickupLeadMinutes = 5;

    public static bool IsValidRange(DateTime pickupDate, DateTime returnDate) =>
        IsValidRange(pickupDate, returnDate, MinimumPickupLeadMinutes);

    public static bool IsValidRange(
        DateTime pickupDate,
        DateTime returnDate,
        int minimumPickupLeadMinutes)
    {
        var normalizedLeadMinutes = Math.Max(0, minimumPickupLeadMinutes);
        var minimumPickupTime = DateTime.Now.AddMinutes(normalizedLeadMinutes);
        return pickupDate >= minimumPickupTime && pickupDate < returnDate;
    }

    public static bool Overlaps(
        DateTime newPickup,
        DateTime newReturn,
        DateTime existingPickup,
        DateTime existingReturn) =>
        newPickup < existingReturn && newReturn > existingPickup;
}
