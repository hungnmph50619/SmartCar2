using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class VehicleDocument
{
    public int VehicleDocumentId { get; set; }
    public int VehicleId { get; set; }
    public VehicleDocumentType DocumentType { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime IssuedDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? ImagePath { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Vehicle Vehicle { get; set; } = null!;
}
