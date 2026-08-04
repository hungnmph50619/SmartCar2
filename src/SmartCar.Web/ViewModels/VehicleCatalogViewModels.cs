using System.ComponentModel.DataAnnotations;

namespace SmartCar.Web.ViewModels;

public sealed class VehicleCatalogQueryViewModel
{
    public string PickupLocation { get; set; } = "SmartCar Cầu Giấy";

    [DataType(DataType.Date)]
    public DateTime? PickupDate { get; set; }

    public string PickupTime { get; set; } = "08:00";

    [DataType(DataType.Date)]
    public DateTime? ReturnDate { get; set; }

    public string ReturnTime { get; set; } = "18:00";

    public string? Brand { get; set; }
    public int? Seats { get; set; }
    public string? Transmission { get; set; }
    public string? FuelType { get; set; }
    public int? ManufactureYearFrom { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool DeliveryAvailable { get; set; }
    public string Sort { get; set; } = "recommended";
}

public sealed class VehicleCatalogViewModel
{
    public VehicleCatalogQueryViewModel Query { get; init; } = new();
    public IReadOnlyList<VehicleCardViewModel> Vehicles { get; init; } = Array.Empty<VehicleCardViewModel>();
    public IReadOnlyList<string> Brands { get; init; } = Array.Empty<string>();
    public int TotalCount { get; init; }
    public DateTime PickupDateTime { get; init; }
    public DateTime ReturnDateTime { get; init; }
    public string SearchSummary { get; init; } = string.Empty;
}

public sealed class VehicleCardViewModel
{
    public int VehicleId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public int ManufactureYear { get; init; }
    public int Seats { get; init; }
    public string Transmission { get; init; } = string.Empty;
    public string FuelType { get; init; } = string.Empty;
    public decimal DailyPrice { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public double Rating { get; init; }
    public int RentalCount { get; init; }
    public string Badge { get; init; } = "Còn trống";
    public bool DeliveryAvailable { get; init; } = true;
    public string PickupLocation { get; init; } = "SmartCar Cầu Giấy";
}

public sealed class VehicleImageItemViewModel
{
    public string Url { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string Category { get; init; } = "Tất cả";
}

public sealed class VehicleReviewItemViewModel
{
    public int Rating { get; init; }
    public string Comment { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string CustomerName { get; init; } = "Khách hàng SmartCar";
}

public sealed class VehicleDetailsViewModel
{
    public int VehicleId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string LicensePlate { get; init; } = string.Empty;
    public int ManufactureYear { get; init; }
    public int Seats { get; init; }
    public string Transmission { get; init; } = string.Empty;
    public string FuelType { get; init; } = string.Empty;
    public string? Color { get; init; }
    public int CurrentMileage { get; init; }
    public decimal DailyPrice { get; init; }
    public string Description { get; init; } = string.Empty;
    public double Rating { get; init; }
    public int RentalCount { get; init; }
    public DateTime PickupDateTime { get; init; }
    public DateTime ReturnDateTime { get; init; }
    public string PickupLocation { get; init; } = "SmartCar Cầu Giấy";
    public int RentalDays { get; init; }
    public decimal RentalAmount { get; init; }
    public decimal DeliveryFee { get; init; }
    public decimal DepositAmount { get; init; }
    public decimal EstimatedTotal { get; init; }
    public IReadOnlyList<VehicleImageItemViewModel> Images { get; init; } = Array.Empty<VehicleImageItemViewModel>();
    public IReadOnlyList<VehicleReviewItemViewModel> Reviews { get; init; } = Array.Empty<VehicleReviewItemViewModel>();
    public IReadOnlyList<VehicleCardViewModel> SimilarVehicles { get; init; } = Array.Empty<VehicleCardViewModel>();
}

public sealed class CreateRentalRequestViewModel
{
    public int VehicleId { get; set; }
    public string PickupLocation { get; set; } = "SmartCar Cầu Giấy";
    public DateTime PickupDate { get; set; }
    public string PickupTime { get; set; } = "08:00";
    public DateTime ReturnDate { get; set; }
    public string ReturnTime { get; set; } = "18:00";
}
