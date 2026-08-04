using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class MaintenanceRecord
{
    public int MaintenanceRecordId { get; set; }
    public int VehicleId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string Content { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public string? ServiceProvider { get; set; }
    public int Mileage { get; set; }
    public MaintenanceStatus Status { get; set; } = MaintenanceStatus.InProgress;

    public Vehicle Vehicle { get; set; } = null!;
}
