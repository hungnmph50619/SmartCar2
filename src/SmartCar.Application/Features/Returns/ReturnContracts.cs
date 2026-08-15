using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Returns;

public sealed record ReturnPreparationDto(
    int BookingId,
    DateTime ScheduledReturnDate,
    string ScheduledReturnLocation,
    DateTime HandoverAt,
    int HandoverMileage,
    string HandoverFuelLevel,
    string? HandoverVehicleImagePaths,
    string? HandoverDocumentImagePaths,
    string VehicleFuelType);

public sealed record CreateReturnRequest(
    int BookingId,
    DateTime ReturnedAt,
    string ReturnLocation,
    int Mileage,
    string FuelLevel,
    string? ExteriorCondition,
    string? InteriorCondition,
    bool HasDamage,
    string? ImagePaths,
    string? Notes);

public sealed record AddChargeRequest(
    int BookingId,
    AdditionalChargeType ChargeType,
    string Description,
    decimal Amount);

public interface IReturnService
{
    Task<ReturnPreparationDto?> GetPreparationAsync(
        int bookingId,
        CancellationToken cancellationToken = default);
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
