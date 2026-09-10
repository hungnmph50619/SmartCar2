using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class VehicleIncident
{
    public int VehicleIncidentId { get; set; }
    public int VehicleId { get; set; }
    public int? BookingId { get; set; }
    public IncidentType IncidentType { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public DateTime OccurredAt { get; set; }
    public string? Location { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal EstimatedCost { get; set; }
    public decimal ActualCost { get; set; }
    public decimal FineAmount { get; set; }
    public decimal CustomerLiabilityAmount { get; set; }
    public string? EvidencePaths { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public Vehicle Vehicle { get; set; } = null!;
    public Booking? Booking { get; set; }
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
