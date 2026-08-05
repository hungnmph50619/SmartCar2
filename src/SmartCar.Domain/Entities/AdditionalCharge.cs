using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class AdditionalCharge
{
    public int AdditionalChargeId { get; set; }
    public int VehicleReturnId { get; set; }
    public AdditionalChargeType ChargeType { get; set; } = AdditionalChargeType.Other;
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    public VehicleReturn VehicleReturn { get; set; } = null!;
}
