using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class Booking
{
    public int BookingId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public int VehicleId { get; set; }
    public DateTime PickupDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public decimal DailyPrice { get; set; }
    public int NumberOfDays { get; set; }
    public decimal RentalAmount { get; set; }
    public decimal AdditionalAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PendingConfirmation;
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public Vehicle Vehicle { get; set; } = null!;
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public VehicleHandover? Handover { get; set; }
    public VehicleReturn? VehicleReturn { get; set; }
    public Review? Review { get; set; }
}
