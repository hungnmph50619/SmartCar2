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
            return OperationResult.Failure("Chỉ đơn đã thanh toán nhưng chưa giao xe mới được ghi nhận khách không đến nhận.");
        }

        if (DateTime.Now < booking.PickupDate.AddMinutes(30))
        {
            return OperationResult.Failure(
                "Chỉ được ghi nhận khách không đến nhận xe sau giờ nhận ít nhất 30 phút.");
        }

        booking.Status = BookingStatus.NoShow;
        booking.NoShowMarkedAt = DateTime.UtcNow;
        booking.CancelledBy = "Quản trị viên";
        booking.CancelReason = "Khách không đến nhận xe đúng thời gian quy định.";
        booking.RefundAmount = 0;
        booking.RefundReason = "Khách không đến nhận xe nên không được hoàn tiền.";
        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê ghi nhận khách không đến nhận xe",
            Message = $"Đơn #{booking.BookingId} đã được ghi nhận khách không đến nhận xe và không được hoàn tiền."
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

        var refundAmount = isAdmin ? paidAmount : 0m;
        var refundReason = isAdmin
            ? "Quản trị viên hủy đơn trước khi giao xe: hoàn 100% số tiền khách đã thanh toán."
            : paidAmount > 0
                ? "Khách hàng chủ động hủy sau khi thanh toán: không hoàn tiền."
                : "Đơn chưa phát sinh thanh toán nên không có khoản hoàn tiền.";

        booking.Status = BookingStatus.Cancelled;
        booking.CancelReason = request.Reason.Trim();
        booking.CancelledBy = isAdmin ? "Quản trị viên" : "Khách hàng";
        booking.CancelledAt = DateTime.UtcNow;
        booking.RefundAmount = refundAmount;
        booking.RefundReason = refundReason;
        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

        if (refundAmount > 0 && !booking.Payments.Any(payment => payment.Type == PaymentType.Refund))
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = refundAmount,
                Method = "Mô phỏng",
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow,
                TransactionCode = $"RF{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}"
            });

            if (!string.IsNullOrWhiteSpace(booking.PromotionCode))
            {
                var promotion = await _dbContext.Promotions.FirstOrDefaultAsync(
                    item => item.Code == booking.PromotionCode,
                    cancellationToken);
                if (promotion is not null && promotion.UsedCount > 0)
                {
                    promotion.UsedCount--;
                }
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được hủy",
            Message = refundAmount > 0
                ? $"Đơn #{booking.BookingId} đã hủy. Số tiền hoàn: {refundAmount:N0} đồng."
                : $"Đơn #{booking.BookingId} đã hủy và không được hoàn tiền."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RefundResult.Success(refundAmount);
    }
}
