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

    // Only the Staff counter flow supplies this flag. The proposed pickup is captured
    // when the request arrives; allow a short processing delay without scheduling in the past.
    public static bool IsValidImmediateCounterRange(DateTime pickupDate, DateTime returnDate)
    {
        var now = DateTime.Now;
        return pickupDate >= now.AddMinutes(-2) &&
               pickupDate <= now.AddMinutes(1) &&
               returnDate > now &&
               returnDate > pickupDate;
    }

    public static bool Overlaps(
        DateTime newPickup,
        DateTime newReturn,
        DateTime existingPickup,
        DateTime existingReturn) =>
        newPickup < existingReturn && newReturn > existingPickup;
}
