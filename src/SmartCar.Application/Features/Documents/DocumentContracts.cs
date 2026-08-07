using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Documents;

public sealed record SubmitDocumentRequest(
    string DocumentType,
    string DocumentNumber,
    DateTime? ExpiryDate,
    string ImagePath);

public sealed record SubmitCitizenIdRequest(
    string FullNameOnDocument,
    string DocumentNumber,
    DateTime DateOfBirth,
    string Gender,
    DateTime IssuedDate,
    DateTime ExpiryDate,
    string PermanentAddress,
    string FrontImagePath,
    string BackImagePath);

public sealed record SubmitDrivingLicenseRequest(
    string FullNameOnDocument,
    string DocumentNumber,
    string LicenseClass,
    DateTime IssuedDate,
    DateTime ExpiryDate,
    string FrontImagePath,
    string BackImagePath);

public sealed record DocumentDto(
    int CustomerDocumentId,
    string CustomerId,
    string CustomerName,
    string? CustomerPhone,
    string DocumentType,
    string DocumentNumber,
    DateTime? ExpiryDate,
    string ImagePath,
    DocumentStatus Status,
    string? RejectionReason,
    string? VerifiedBy,
    DateTime? VerifiedAt,
    DateTime UpdatedAt,
    string? FullNameOnDocument,
    DateTime? DateOfBirth,
    string? Gender,
    DateTime? IssuedDate,
    string? PermanentAddress,
    string? LicenseClass,
    bool HasRequiredData);

public interface IDocumentService
{
    Task<IReadOnlyList<DocumentDto>> GetCustomerDocumentsAsync(
        string customerId,
        CancellationToken cancellationToken = default);
    Task<DocumentDto?> GetDocumentAsync(
        int documentId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentDto>> GetPendingDocumentsAsync(
        CancellationToken cancellationToken = default);
    Task<OperationResult> SubmitAsync(
        string customerId,
        SubmitDocumentRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> SubmitCitizenIdAsync(
        string customerId,
        SubmitCitizenIdRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> SubmitDrivingLicenseAsync(
        string customerId,
        SubmitDrivingLicenseRequest request,
        CancellationToken cancellationToken = default);
    Task<OperationResult> VerifyAsync(
        int documentId,
        string adminId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> RejectAsync(
        int documentId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default);
    Task<bool> HasValidRentalDocumentsAsync(
        string customerId,
        DateTime rentalDate,
        CancellationToken cancellationToken = default);
}
