using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Incidents;

public sealed record IncidentDto(
    int VehicleIncidentId,
    int VehicleId,
    string VehicleName,
    string LicensePlate,
    int? BookingId,
    IncidentType IncidentType,
    IncidentStatus Status,
    DateTime OccurredAt,
    string? Location,
    string Description,
    decimal EstimatedCost,
    decimal ActualCost,
    decimal FineAmount,
    decimal CustomerLiabilityAmount,
    string? EvidencePaths,
    string? Notes,
    DateTime? ResolvedAt);

public sealed record CreateIncidentRequest(
    int VehicleId,
    int? BookingId,
    IncidentType IncidentType,
    DateTime OccurredAt,
    string? Location,
    string Description,
    decimal EstimatedCost,
    decimal FineAmount,
    decimal CustomerLiabilityAmount,
    string? EvidencePaths,
    string? Notes);

public sealed record ResolveIncidentRequest(
    int IncidentId,
    decimal ActualCost,
    decimal FineAmount,
    decimal CustomerLiabilityAmount,
    string? Notes,
    bool RequiresMaintenance);

public interface IIncidentService
{
    Task<IReadOnlyList<IncidentDto>> GetAllAsync(IncidentStatus? status = null, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateAsync(CreateIncidentRequest request, string adminId, CancellationToken cancellationToken = default);
    Task<OperationResult> StartInvestigationAsync(int incidentId, string adminId, CancellationToken cancellationToken = default);
    Task<OperationResult> ResolveAsync(ResolveIncidentRequest request, string adminId, CancellationToken cancellationToken = default);
}
