using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Extensions;

public sealed record RequestExtensionRequest(
    int BookingId,
    DateTime RequestedReturnDate,
    string? CustomerNote);

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
    DateTime RequestedAt);

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
