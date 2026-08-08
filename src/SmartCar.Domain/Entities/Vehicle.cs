using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class Vehicle
{
    public int VehicleId { get; set; }
    public int BrandId { get; set; }
    public string VehicleName { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public int ManufactureYear { get; set; }
    public int Seats { get; set; }
    public string Transmission { get; set; } = string.Empty;
    public string FuelType { get; set; } = string.Empty;
    public string? Color { get; set; }
    public decimal DailyPrice { get; set; }
    public string? PickupAddress { get; set; }
    public int CurrentMileage { get; set; }
    public VehicleStatus Status { get; set; } = VehicleStatus.Available;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public Brand Brand { get; set; } = null!;
    public ICollection<VehicleImage> Images { get; set; } = new List<VehicleImage>();
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<MaintenanceRecord> MaintenanceRecords { get; set; } = new List<MaintenanceRecord>();
    public ICollection<VehicleDocument> Documents { get; set; } = new List<VehicleDocument>();
    public ICollection<VehicleIncident> Incidents { get; set; } = new List<VehicleIncident>();
}
