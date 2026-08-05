using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class BookingOperationService : IBookingOperationService
{
    private readonly ApplicationDbContext _dbContext;

    public BookingOperationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<RefundResult> CancelByCustomerAsync(
        string customerId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default) =>
        CancelAsync(customerId, false, request, cancellationToken);

    public Task<RefundResult> CancelByAdminAsync(
        string adminId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default) =>
        CancelAsync(adminId, true, request, cancellationToken);

    public async Task<OperationResult> MarkNoShowAsync(
        int bookingId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status is not (BookingStatus.Paid or BookingStatus.ReadyForPickup))
        {
            return OperationResult.Failure("Chỉ đơn đã thanh toán nhưng chưa giao xe mới được đánh dấu không đến nhận.");
        }

        if (DateTime.Now < booking.PickupDate.AddMinutes(30))
        {
            return OperationResult.Failure("Chỉ được đánh dấu no-show sau giờ nhận xe ít nhất 30 phút.");
        }

        booking.Status = BookingStatus.NoShow;
        booking.NoShowMarkedAt = DateTime.UtcNow;
        booking.CancelledBy = "Admin";
        booking.CancelReason = "Khách không đến nhận xe đúng thời gian quy định.";
        booking.RefundAmount = 0;
        booking.RefundReason = "No-show không được hoàn tiền.";
        booking.Vehicle.Status = VehicleStatus.Available;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã bị ghi nhận no-show",
            Message = $"Đơn #{booking.BookingId} đã bị ghi nhận không đến nhận xe và không được hoàn tiền."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    private async Task<RefundResult> CancelAsync(
        string actorId,
        bool isAdmin,
        CancelBookingRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return RefundResult.Failure("Vui lòng nhập lý do hủy đơn.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var query = _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .AsQueryable();

        if (!isAdmin)
        {
            query = query.Where(item => item.CustomerId == actorId);
        }

        var booking = await query
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return RefundResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status is BookingStatus.Rented or BookingStatus.PendingInspection or
            BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.NoShow or BookingStatus.Rejected)
        {
            return RefundResult.Failure("Trạng thái hiện tại không cho phép hủy đơn.");
        }

        var paidAmount = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type is PaymentType.Rental or PaymentType.Extension)
            .Sum(payment => payment.Amount);

        decimal refundAmount;
        string refundReason;

        if (isAdmin)
        {
            refundAmount = paidAmount;
            refundReason = "Admin hủy đơn: hoàn 100% số tiền đã thanh toán.";
        }
        else if (paidAmount <= 0)
        {
            refundAmount = 0;
            refundReason = "Đơn chưa phát sinh thanh toán.";
        }
        else if (booking.PickupDate - DateTime.Now >= TimeSpan.FromHours(24))
        {
            refundAmount = paidAmount;
            refundReason = "Khách hủy trước giờ nhận xe ít nhất 24 giờ: hoàn 100%.";
        }
        else
        {
            refundAmount = Math.Round(paidAmount * 0.5m, 0, MidpointRounding.AwayFromZero);
            refundReason = "Khách hủy trong vòng 24 giờ trước giờ nhận xe: hoàn 50%.";
        }

        booking.Status = BookingStatus.Cancelled;
        booking.CancelReason = request.Reason.Trim();
        booking.CancelledBy = isAdmin ? "Admin" : "Customer";
        booking.CancelledAt = DateTime.UtcNow;
        booking.RefundAmount = refundAmount;
        booking.RefundReason = refundReason;
        booking.Vehicle.Status = VehicleStatus.Available;

        if (refundAmount > 0 && !booking.Payments.Any(payment => payment.Type == PaymentType.Refund))
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = refundAmount,
                Method = "Mo phong",
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow,
                TransactionCode = $"RF{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}"
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được hủy",
            Message = refundAmount > 0
                ? $"Đơn #{booking.BookingId} đã hủy. Số tiền hoàn: {refundAmount:N0} đồng."
                : $"Đơn #{booking.BookingId} đã hủy và không phát sinh hoàn tiền."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RefundResult.Success(refundAmount);
    }
}
