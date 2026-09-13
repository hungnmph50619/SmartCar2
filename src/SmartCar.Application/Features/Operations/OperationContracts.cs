using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Operations;

public sealed record CancelBookingRequest(
    int BookingId,
    string Reason);

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

    Task<RefundResult> CancelByStaffAsync(
        string staffId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default);

    // Admin vẫn cần API này cho các quyết định cấp quản trị như xử lý xung đột/gia hạn.
    // Luồng xử lý đơn thông thường không gọi phương thức này nữa.
    Task<RefundResult> CancelByAdminAsync(
        string adminId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> MarkNoShowAsync(
        int bookingId,
        string staffId,
        bool customerContacted,
        CancellationToken cancellationToken = default);
}
