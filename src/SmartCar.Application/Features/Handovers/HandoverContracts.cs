using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Handovers;

public sealed record HandoverVehicleContextDto(
    int CurrentMileage,
    string FuelType);

public sealed record CreateHandoverRequest(
    int BookingId,
    DateTime HandoverAt,
    int Mileage,
    string FuelLevel,
    string? ExteriorCondition,
    string? InteriorCondition,
    string? ImagePaths,
    string? Notes);

public interface IHandoverService
{
    Task<HandoverVehicleContextDto?> GetVehicleContextAsync(
        int bookingId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default);
}
