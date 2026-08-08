namespace SmartCar.Application.Features.Bookings;

public static class BookingDateRules
{
    public static bool IsValidRange(
        DateTime pickupDate,
        DateTime returnDate)
    {
        var minimumPickupTime = DateTime.Now.AddMinutes(5);

        return pickupDate >= minimumPickupTime
               && pickupDate < returnDate;
    }

    public static bool Overlaps(
        DateTime newPickup,
        DateTime newReturn,
        DateTime existingPickup,
        DateTime existingReturn)
    {
        return newPickup < existingReturn
               && newReturn > existingPickup;
    }

    public static int CalculateNumberOfDays(
        DateTime pickupDate,
        DateTime returnDate)
    {
        var totalHours =
            (returnDate - pickupDate).TotalHours;

        return Math.Max(
            1,
            (int)Math.Ceiling(totalHours / 24d));
    }
}