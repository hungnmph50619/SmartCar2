using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Maintenance;

public sealed record MaintenanceDto(
    int MaintenanceRecordId,
    int VehicleId,
    string VehicleName,
    string LicensePlate,
    DateTime StartDate,
    DateTime? CompletedDate,
    string Content,
    decimal Cost,
    string? ServiceProvider,
    int Mileage,
    MaintenanceStatus Status);

public sealed record CreateMaintenanceRequest(
    int VehicleId,
    DateTime StartDate,
    string Content,
    decimal Cost,
    string? ServiceProvider,
    int Mileage);

public interface IMaintenanceService
{
    Task<IReadOnlyList<MaintenanceDto>> GetAllAsync(
        CancellationToken cancellationToken = default);
    Task<MaintenanceDto?> GetByIdAsync(
        int maintenanceId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> CreateAsync(
        CreateMaintenanceRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> CompleteAsync(
        int maintenanceId,
        decimal finalCost,
        string? completionNote,
        CancellationToken cancellationToken = default);
    Task<OperationResult> CancelAsync(
        int maintenanceId,
        string reason,
        CancellationToken cancellationToken = default);
}
