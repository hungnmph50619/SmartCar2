namespace SmartCar.Domain.Entities;

public class VehicleReturn
{
    public int VehicleReturnId { get; set; }
    public int BookingId { get; set; }
    public DateTime ReturnedAt { get; set; }
    public int Mileage { get; set; }
    public string FuelLevel { get; set; } = string.Empty;
    public string? ExteriorCondition { get; set; }
    public string? InteriorCondition { get; set; }
    public bool HasDamage { get; set; }
    public string? ImagePaths { get; set; }
    public string? Notes { get; set; }

    public Booking Booking { get; set; } = null!;
    public ICollection<AdditionalCharge> AdditionalCharges { get; set; } = new List<AdditionalCharge>();
}
