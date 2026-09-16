using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Returns;

public sealed record CreateReturnRequest(
    int BookingId,
    DateTime ReturnedAt,
    int Mileage,
    string FuelLevel,
    string? ExteriorCondition,
    string? InteriorCondition,
    bool HasDamage,
    string? ImagePaths,
    string? Notes,
    string IdentityVerifiedByStaffId,
    Guid IdentityFaceSessionId);

public sealed record AddChargeRequest(
    int BookingId,
    AdditionalChargeType ChargeType,
    string Description,
    decimal Amount);

public interface IReturnService
{
    Task<OperationResult> CreateAsync(
        CreateReturnRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> AddChargeAsync(
        AddChargeRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> RemoveChargeAsync(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> CompleteAsync(
        int bookingId,
        bool requiresMaintenance,
        string? maintenanceNote,
        CancellationToken cancellationToken = default);
}
