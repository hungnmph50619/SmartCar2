using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Operations;

public sealed record CancelBookingRequest(
    int BookingId,
    string Reason);

public sealed record MarkNoShowRequest(
    int BookingId,
    bool ContactAttempted,
    bool ArrivedAtPickupLocation,
    string ContactNote);

public sealed record NoShowPreparationDto(
    int BookingId,
    DateTime PickupDate,
    string PickupMethod,
    string PickupLocation,
    string? CustomerPhone);

public sealed record RefundResult(
    bool Succeeded,
    decimal RefundAmount,
    IReadOnlyCollection<string> Errors)
{
    public static RefundResult Success(decimal refundAmount) =>
        new(true, refundAmount, Array.Empty<string>());

    public static RefundResult Failure(params string[] errors) =>
        new(false, 0, errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());
}

public interface IBookingOperationService
{
    Task<RefundResult> CancelByCustomerAsync(
        string customerId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default);
    Task<RefundResult> CancelByAdminAsync(
        string adminId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default);
    Task<NoShowPreparationDto?> GetNoShowPreparationAsync(
        int bookingId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> MarkNoShowAsync(
        MarkNoShowRequest request,
        string adminId,
        CancellationToken cancellationToken = default);
}
