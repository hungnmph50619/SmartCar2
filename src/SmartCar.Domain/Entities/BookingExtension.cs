using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class BookingExtension
{
    public int BookingExtensionId { get; set; }
    public int BookingId { get; set; }
    public DateTime OriginalReturnDate { get; set; }
    public DateTime RequestedReturnDate { get; set; }
    public int AdditionalDays { get; set; }
    public decimal AdditionalAmount { get; set; }
    public BookingExtensionStatus Status { get; set; } = BookingExtensionStatus.Pending;
    public string? CustomerNote { get; set; }
    public string? AdminNote { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
    public DateTime? PaidAt { get; set; }

    public Booking Booking { get; set; } = null!;
}
