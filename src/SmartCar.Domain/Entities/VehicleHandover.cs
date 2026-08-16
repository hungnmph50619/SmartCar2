namespace SmartCar.Domain.Entities;

public class VehicleHandover
{
    public int VehicleHandoverId { get; set; }
    public int BookingId { get; set; }
    public DateTime HandoverAt { get; set; }
    public int Mileage { get; set; }
    public string FuelLevel { get; set; } = string.Empty;
    public string? ExteriorCondition { get; set; }
    public string? InteriorCondition { get; set; }
    public string? Accessories { get; set; }
    public string? ImagePaths { get; set; }
    public int IncludedKilometers { get; set; }
    public decimal ExcessKmFeePerKm { get; set; }
    public decimal LateReturnFeeMultiplier { get; set; }
    public string TrafficFineTerms { get; set; } = string.Empty;
    public string DamageCompensationTerms { get; set; } = string.Empty;
    public bool PenaltyPolicyAccepted { get; set; }
    public string? Notes { get; set; }

    public Booking Booking { get; set; } = null!;
}
