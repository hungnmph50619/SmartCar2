using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Payments;

public sealed record AdminPaymentListItemDto(
    int PaymentId,
    int BookingId,
    string CustomerId,
    string CustomerName,
    string VehicleName,
    string LicensePlate,
    PaymentType Type,
    decimal Amount,
    string Method,
    PaymentStatus Status,
    DateTime? PaidAt,
    string? TransactionCode);

public interface IPaymentService
{
    Task<IReadOnlyList<AdminPaymentListItemDto>> GetAdminPaymentsAsync(
        PaymentStatus? status = null,
        PaymentType? type = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> SubmitQrPaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        string actorId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ConfirmQrPaymentAsync(
        int paymentId,
        string actorId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RejectQrPaymentAsync(
        int paymentId,
        string actorId,
        string rejectionReason,
        CancellationToken cancellationToken = default);
}
