namespace SmartCar.Web.ViewModels;

public sealed class BookingConfirmViewModel
{
    public int VehicleId { get; set; }

    public string VehicleName { get; set; }
        = string.Empty;

    public string BrandName { get; set; }
        = string.Empty;

    public string LicensePlate { get; set; }
        = string.Empty;

    public string? PrimaryImagePath { get; set; }

    public DateTime PickupDate { get; set; }

    public DateTime ReturnDate { get; set; }

    public int NumberOfDays { get; set; }

    public decimal DailyPrice { get; set; }

    public decimal TotalAmount { get; set; }
}