using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.VehicleDocuments;

public sealed record VehicleDocumentDto(
    int VehicleDocumentId,
    int VehicleId,
    string VehicleName,
    string LicensePlate,
    VehicleDocumentType DocumentType,
    string DocumentNumber,
    DateTime IssuedDate,
    DateTime? ExpiryDate,
    string? ImagePath,
    string? Notes,
    bool IsExpired,
    int? DaysUntilExpiry);

public sealed record SaveVehicleDocumentRequest(
    int VehicleId,
    VehicleDocumentType DocumentType,
    string DocumentNumber,
    DateTime IssuedDate,
    DateTime? ExpiryDate,
    string? ImagePath,
    string? Notes);

public interface IVehicleDocumentService
{
    Task<IReadOnlyList<VehicleDocumentDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VehicleDocumentDto>> GetByVehicleAsync(int vehicleId, CancellationToken cancellationToken = default);
    Task<OperationResult> CreateAsync(SaveVehicleDocumentRequest request, string adminId, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(int documentId, string adminId, CancellationToken cancellationToken = default);
}
