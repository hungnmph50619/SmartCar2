namespace SmartCar.Domain.Entities;

public class VehicleImage
{
    public int VehicleImageId { get; set; }
    public int VehicleId { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }

    public Vehicle Vehicle { get; set; } = null!;
}
