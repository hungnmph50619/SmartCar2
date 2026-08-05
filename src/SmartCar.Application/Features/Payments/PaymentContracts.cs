using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Payments;

public interface IPaymentService
{
    Task<OperationResult> SimulatePaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        CancellationToken cancellationToken = default);
}
