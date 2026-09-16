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
    int IncludedKilometers,
    decimal ExcessKmFeePerKm,
    decimal LateReturnFeeMultiplier,
    string TrafficFineTerms,
    string DamageCompensationTerms,
    bool PenaltyPolicyAccepted,
    string? Notes,
    string IdentityVerifiedByStaffId,
    Guid IdentityFaceSessionId);

public interface IHandoverService
{
    Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default);
}
