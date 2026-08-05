using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Handovers;

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

public interface IHandoverService
{
    Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default);
}
