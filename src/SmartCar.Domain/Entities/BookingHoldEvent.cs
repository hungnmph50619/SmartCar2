namespace SmartCar.Domain.Entities;

public sealed class BookingHoldEvent
{
    public int BookingHoldEventId { get; set; }
    public int BookingId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public int VehicleId { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public bool IsWaived { get; set; }
    public string? WaivedByAdminId { get; set; }
    public DateTime? WaivedAt { get; set; }
    public string? WaiveReason { get; set; }
}
