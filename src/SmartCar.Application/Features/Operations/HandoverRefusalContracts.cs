namespace SmartCar.Application.Features.Operations;

public sealed record HandoverRefusalPreviewDto(
    int BookingId,
    decimal PaidRentalAmount,
    decimal PaidDepositAmount,
    decimal RentalPenaltyAmount,
    decimal IncurredPickupDeliveryFee,
    decimal RetainedAmount,
    decimal RentalRefundAmount,
    decimal TotalRefundAmount);

public interface IHandoverRefusalService
{
    Task<HandoverRefusalPreviewDto?> GetPreviewAsync(
        int bookingId,
        CancellationToken cancellationToken = default);

    Task<RefundResult> CancelAsync(
        string adminId,
        int bookingId,
        CancellationToken cancellationToken = default);
}
