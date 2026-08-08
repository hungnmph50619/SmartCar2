using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Vehicles;

public sealed class VehicleDto
{
    public int VehicleId { get; init; }
    public int BrandId { get; init; }
    public string BrandName { get; init; } = string.Empty;
    public string VehicleName { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string LicensePlate { get; init; } = string.Empty;
    public int ManufactureYear { get; init; }
    public int Seats { get; init; }
    public string Transmission { get; init; } = string.Empty;
    public string FuelType { get; init; } = string.Empty;
    public string? Color { get; init; }
    public decimal DailyPrice { get; init; }
    public string? PickupAddress { get; init; }
    public int CurrentMileage { get; init; }
    public VehicleStatus Status { get; init; }
    public string? Description { get; init; }
    public string? PrimaryImagePath { get; init; }
    public byte[] RowVersion { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<VehicleImageDto> Images { get; init; } = Array.Empty<VehicleImageDto>();
}

public sealed record VehicleImageDto(
    int VehicleImageId,
    string ImagePath,
    bool IsPrimary,
    int SortOrder);

public sealed record VehicleSearchRequest(
    DateTime PickupDate,
    DateTime ReturnDate,
    int? BrandId = null,
    int? Seats = null,
    string? Transmission = null,
    decimal? MaxDailyPrice = null,
    string? PickupAddress = null);

public sealed record CreateVehicleRequest(
    int BrandId,
    string VehicleName,
    string? Model,
    string LicensePlate,
    int ManufactureYear,
    int Seats,
    string Transmission,
    string FuelType,
    string? Color,
    decimal DailyPrice,
    int CurrentMileage,
    string? PickupAddress,
    string? Description);

public sealed record UpdateVehicleRequest(
    int VehicleId,
    int BrandId,
    string VehicleName,
    string? Model,
    string LicensePlate,
    int ManufactureYear,
    int Seats,
    string Transmission,
    string FuelType,
    string? Color,
    decimal DailyPrice,
    int CurrentMileage,
    string? PickupAddress,
    string? Description,
    byte[] RowVersion);

public sealed class VehicleMutationResult
{
    private VehicleMutationResult(bool succeeded, int? vehicleId, IReadOnlyCollection<string> errors)
    {
        Succeeded = succeeded;
        VehicleId = vehicleId;
        Errors = errors;
    }

    public bool Succeeded { get; }
    public int? VehicleId { get; }
    public IReadOnlyCollection<string> Errors { get; }

    public static VehicleMutationResult Success(int vehicleId) =>
        new(true, vehicleId, Array.Empty<string>());

    public static VehicleMutationResult Failure(params string[] errors) =>
        new(false, null, errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());
}

public interface IVehicleService
{
    Task<IReadOnlyList<VehicleDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<VehicleDto?> GetByIdAsync(int vehicleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VehicleDto>> SearchAvailableAsync(
        VehicleSearchRequest request,
        CancellationToken cancellationToken = default);
    Task<VehicleMutationResult> CreateAsync(
        CreateVehicleRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateAsync(
        UpdateVehicleRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> ChangeStatusAsync(
        int vehicleId,
        VehicleStatus status,
        CancellationToken cancellationToken = default);
    Task<OperationResult> AddImageAsync(
        int vehicleId,
        string imagePath,
        bool setAsPrimary,
        CancellationToken cancellationToken = default);
    Task<OperationResult> SetPrimaryImageAsync(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteImageAsync(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken = default);
}
