using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class PaymentService : IPaymentService
{
    private readonly ApplicationDbContext _dbContext;

    public PaymentService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationResult> SimulatePaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (paymentType == PaymentType.Rental && booking.Status != BookingStatus.PendingPayment)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán tiền thuê.");
        }

        if (paymentType == PaymentType.AdditionalCharge &&
            booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán phụ phí.");
        }

        var payment = booking.Payments
            .FirstOrDefault(item => item.Type == paymentType && item.Status == PaymentStatus.Pending);

        if (payment is null && paymentType == PaymentType.AdditionalCharge && booking.AdditionalAmount > 0)
        {
            payment = new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.AdditionalCharge,
                Amount = booking.AdditionalAmount,
                Method = "Mo phong",
                Status = PaymentStatus.Pending
            };
            booking.Payments.Add(payment);
        }

        if (payment is null)
        {
            var alreadyPaid = booking.Payments.Any(item =>
                item.Type == paymentType && item.Status == PaymentStatus.Paid);

            return alreadyPaid
                ? OperationResult.Failure("Khoản tiền này đã được thanh toán.")
                : OperationResult.Failure("Không tìm thấy khoản thanh toán phù hợp.");
        }

        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = $"SC{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        if (paymentType == PaymentType.Rental)
        {
            booking.Status = BookingStatus.Paid;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Thanh toán thành công",
            Message = paymentType == PaymentType.Rental
                ? $"Đơn #{booking.BookingId} đã thanh toán tiền thuê thành công."
                : $"Đơn #{booking.BookingId} đã thanh toán phụ phí thành công."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }
}
