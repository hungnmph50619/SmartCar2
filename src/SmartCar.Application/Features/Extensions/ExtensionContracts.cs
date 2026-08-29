using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Extensions;

public sealed record RequestExtensionRequest(
    int BookingId,
    DateTime RequestedReturnDate,
    string? CustomerNote,
    bool IsForceMajeure = false,
    string? EvidenceNote = null);

public sealed record ExtensionDto(
    int BookingExtensionId,
    int BookingId,
    string CustomerId,
    string CustomerName,
    string VehicleName,
    DateTime OriginalReturnDate,
    DateTime RequestedReturnDate,
    int AdditionalDays,
    decimal AdditionalAmount,
    BookingExtensionStatus Status,
    string? CustomerNote,
    string? AdminNote,
    DateTime RequestedAt,
    bool IsForceMajeure,
    string? EvidenceNote,
    bool HasScheduleConflict,
    int? ConflictingBookingId,
    DateTime? ConflictingPickupDate)
{
    // Constructor tương thích với call site cũ chưa truyền thông tin bất khả kháng/xung đột.
    public ExtensionDto(
        int bookingExtensionId,
        int bookingId,
        string customerId,
        string customerName,
        string vehicleName,
        DateTime originalReturnDate,
        DateTime requestedReturnDate,
        int additionalDays,
        decimal additionalAmount,
        BookingExtensionStatus status,
        string? customerNote,
        string? adminNote,
        DateTime requestedAt)
        : this(
            bookingExtensionId,
            bookingId,
            customerId,
            customerName,
            vehicleName,
            originalReturnDate,
            requestedReturnDate,
            additionalDays,
            additionalAmount,
            status,
            customerNote,
            adminNote,
            requestedAt,
            false,
            null,
            false,
            null,
            null)
    {
    }
}

public interface IExtensionService
{
    Task<IReadOnlyList<ExtensionDto>> GetCustomerExtensionsAsync(
        string customerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExtensionDto>> GetPendingExtensionsAsync(
        CancellationToken cancellationToken = default);

    Task<OperationResult> RequestAsync(
        string customerId,
        RequestExtensionRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ApproveAsync(
        int extensionId,
        string adminId,
        bool confirmConflictHandled = false,
        string? adminNote = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RequestMoreEvidenceAsync(
        int extensionId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SupplementEvidenceAsync(
        int extensionId,
        string customerId,
        string evidenceNote,
        string? customerNote = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RejectAsync(
        int extensionId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<OperationResult> MarkPaidAsync(
        int bookingId,
        CancellationToken cancellationToken = default);
}
