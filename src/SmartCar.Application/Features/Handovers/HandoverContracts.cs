using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Handovers;

public sealed record HandoverVehicleContextDto(
    int CurrentMileage,
    string FuelType);

public sealed record CustomerHandoverSnapshotDto(
    int BookingId,
    string CustomerName,
    string VehicleName,
    string LicensePlate,
    DateTime PickupDate,
    DateTime ReturnDate,
    DateTime HandoverAt,
    int Mileage,
    string FuelLevel,
    string? Accessories,
    string? Notes,
    IReadOnlyList<string> ImagePaths,
    bool CanCustomerSign);

public sealed record CreateHandoverRequest(
    int BookingId,
    DateTime HandoverAt,
    int Mileage,
    string FuelLevel,
    string? ExteriorCondition,
    string? InteriorCondition,
    string? Accessories,
    string? ImagePaths,
    string? Notes);

public sealed record ConfirmCustomerHandoverRequest(
    int BookingId,
    string CustomerId,
    string SnapshotHash,
    string SignaturePath,
    string? IpAddress,
    string? UserAgent);

public interface IHandoverService
{
    Task<HandoverVehicleContextDto?> GetVehicleContextAsync(
        int bookingId,
        CancellationToken cancellationToken = default);

    Task<CustomerHandoverSnapshotDto?> GetCustomerSnapshotAsync(
        int bookingId,
        string customerId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ConfirmCustomerSignatureAsync(
        ConfirmCustomerHandoverRequest request,
        CancellationToken cancellationToken = default);
}
