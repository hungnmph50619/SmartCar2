namespace SmartCar.Web.ViewModels;

public sealed class ExtensionConflictResolutionViewModel
{
    public SmartCar.Domain.Constants.RentalPolicySnapshot Policy { get; init; } = new();
    public decimal CurrentDepositAmount { get; init; }
    public int ExtensionId { get; init; }
    public int BookingId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public DateTime PickupDate { get; init; }
    public DateTime ReturnDate { get; init; }

    public IReadOnlyList<ExtensionAlternativeVehicleViewModel> Alternatives { get; init; }
        = Array.Empty<ExtensionAlternativeVehicleViewModel>();
}

public sealed class ExtensionAlternativeVehicleViewModel
{
    public int VehicleId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public decimal DailyPrice { get; init; }
    public decimal EstimatedRentalAmount { get; init; }
    public decimal CurrentRentalAmount { get; init; }
    public decimal PriceDifference => EstimatedRentalAmount - CurrentRentalAmount;
    public bool IsEquivalentPrice => PriceDifference == 0;
}

