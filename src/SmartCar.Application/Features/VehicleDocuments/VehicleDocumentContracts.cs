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


public sealed record VehicleLegalStatusDto(
    bool IsEligible,
    IReadOnlyList<string> Reasons);

public sealed record VehicleDocumentOverviewDto(
    int VehicleId,
    VehicleDocumentDto? Registration,
    VehicleDocumentDto? Inspection,
    VehicleDocumentDto? Insurance,
    VehicleDocumentDto? RoadFee,
    VehicleLegalStatusDto LegalStatus)
{
    public int ExistingRequiredDocumentCount =>
        new VehicleDocumentDto?[] { Registration, Inspection, Insurance, RoadFee }
            .Count(document => document is not null);

    public VehicleDocumentDto? GetDocument(VehicleDocumentType documentType) => documentType switch
    {
        VehicleDocumentType.Registration => Registration,
        VehicleDocumentType.Inspection => Inspection,
        VehicleDocumentType.Insurance => Insurance,
        VehicleDocumentType.RoadFee => RoadFee,
        _ => null
    };
}

public sealed record SaveVehicleDocumentRequest(
    int VehicleId,
    VehicleDocumentType DocumentType,
    string DocumentNumber,
    DateTime IssuedDate,
    DateTime? ExpiryDate,
    string? ImagePath,
    string? Notes);

public sealed record UpdateVehicleDocumentRequest(
    int VehicleDocumentId,
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
    Task<VehicleDocumentDto?> GetByIdAsync(int documentId, CancellationToken cancellationToken = default);
    Task<VehicleDocumentOverviewDto> GetOverviewByVehicleAsync(int vehicleId, CancellationToken cancellationToken = default);
    Task<VehicleLegalStatusDto> GetRentalLegalStatusAsync(
        int vehicleId,
        DateTime pickupDate,
        DateTime returnDate,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<int, VehicleDocumentOverviewDto>> GetOverviewsByVehicleIdsAsync(
        IReadOnlyCollection<int> vehicleIds,
        CancellationToken cancellationToken = default);
    Task<OperationResult> CreateAsync(SaveVehicleDocumentRequest request, string adminId, CancellationToken cancellationToken = default);
    Task<OperationResult> UpdateAsync(UpdateVehicleDocumentRequest request, string adminId, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(int documentId, string adminId, CancellationToken cancellationToken = default);
}
