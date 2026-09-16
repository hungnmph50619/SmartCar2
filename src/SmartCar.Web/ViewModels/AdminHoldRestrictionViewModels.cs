namespace SmartCar.Web.ViewModels;

public sealed class AdminHoldRestrictionListViewModel
{
    public IReadOnlyList<AdminHoldRestrictionItemViewModel> Items { get; init; }
        = Array.Empty<AdminHoldRestrictionItemViewModel>();
}

public sealed class AdminHoldRestrictionItemViewModel
{
    public string CustomerId { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string? CustomerEmail { get; init; }
    public string? CustomerPhone { get; init; }
    public int TimeoutCount24Hours { get; init; }
    public DateTime LatestTimeoutAt { get; init; }
    public DateTime? AccountBlockedUntil { get; init; }
    public IReadOnlyList<AdminVehicleCooldownViewModel> VehicleCooldowns { get; init; }
        = Array.Empty<AdminVehicleCooldownViewModel>();
}

public sealed class AdminVehicleCooldownViewModel
{
    public int VehicleId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public int TimeoutCount24Hours { get; init; }
    public DateTime? CooldownUntil { get; init; }
}
