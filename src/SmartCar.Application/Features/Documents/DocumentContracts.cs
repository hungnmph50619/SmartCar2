using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Documents;

public sealed record SubmitDocumentRequest(
    string DocumentType,
    string DocumentNumber,
    DateTime? ExpiryDate,
    string ImagePath);

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
    DateTime UpdatedAt);

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
