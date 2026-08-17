using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class BookingOperationService : IBookingOperationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public BookingOperationService(
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
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
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status is not (BookingStatus.Paid or BookingStatus.ReadyForPickup))
        {
            return OperationResult.Failure(
                "Chỉ đơn đã thanh toán nhưng chưa giao xe mới được ghi nhận khách không đến nhận.");
        }

        if (DateTime.Now < booking.PickupDate.AddMinutes(30))
        {
            return OperationResult.Failure(
                "Chỉ được ghi nhận khách không đến nhận xe sau giờ nhận ít nhất 30 phút.");
        }

        var depositPaid = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type == PaymentType.Deposit)
            .Sum(payment => payment.Amount);

        booking.Status = BookingStatus.NoShow;
        booking.NoShowMarkedAt = DateTime.UtcNow;
        booking.CancelledBy = "Quản trị viên";
        booking.CancelReason = "Khách không đến nhận xe đúng thời gian quy định.";
        booking.RefundAmount = depositPaid;
        booking.RefundReason = depositPaid > 0
            ? $"Không hoàn tiền thuê. Hoàn cọc {depositPaid:N0} đồng vì xe chưa bàn giao."
            : "Không hoàn tiền thuê và không có cọc cần hoàn.";

        var hasDepositRefund = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Refund &&
            payment.Method == PaymentMethods.DepositRefund &&
            payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded);

        if (depositPaid > 0 && !hasDepositRefund)
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = depositPaid,
                Method = PaymentMethods.DepositRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Khách không đến nhận xe",
            Message = depositPaid > 0
                ? $"Đơn #{booking.BookingId}: tiền thuê không hoàn; cọc {depositPaid:N0} đồng đang chờ hoàn."
                : $"Đơn #{booking.BookingId}: tiền thuê không hoàn."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "MarkNoShow",
            nameof(Booking),
            booking.BookingId.ToString(),
            depositPaid > 0
                ? $"Khách không đến nhận. Không hoàn tiền thuê; hoàn cọc {depositPaid:N0} đồng."
                : "Khách không đến nhận. Không hoàn tiền thuê.",
            cancellationToken: cancellationToken);

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

        var booking = await query.FirstOrDefaultAsync(
            item => item.BookingId == request.BookingId,
            cancellationToken);

        if (booking is null)
        {
            return RefundResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status is
            BookingStatus.Rented or
            BookingStatus.PendingInspection or
            BookingStatus.Completed or
            BookingStatus.Cancelled or
            BookingStatus.NoShow or
            BookingStatus.Rejected)
        {
            return RefundResult.Failure("Trạng thái hiện tại không cho phép hủy đơn.");
        }

        var rentalLikePaid = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type is PaymentType.Rental or PaymentType.Extension or PaymentType.VehicleSwapAdjustment)
            .Sum(payment => payment.Amount);

        var depositPaid = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type == PaymentType.Deposit)
            .Sum(payment => payment.Amount);

        decimal refundableRentalAmount;
        string rentalRefundReason;

        if (isAdmin)
        {
            refundableRentalAmount = rentalLikePaid;
            rentalRefundReason = rentalLikePaid > 0
                ? "SmartCar chủ động hủy trước khi giao xe: hoàn 100% tiền thuê đã thu."
                : "Chưa phát sinh tiền thuê đã thu.";
        }
        else
        {
            var hoursBeforePickup = (booking.PickupDate - DateTime.Now).TotalHours;

            if (hoursBeforePickup >= 48)
            {
                refundableRentalAmount = rentalLikePaid;
                rentalRefundReason = rentalLikePaid > 0
                    ? "Hủy trước giờ nhận từ 48 giờ: hoàn 100% tiền thuê."
                    : "Chưa phát sinh tiền thuê đã thu.";
            }
            else if (hoursBeforePickup >= 24)
            {
                refundableRentalAmount = Math.Round(rentalLikePaid * 0.5m, 0);
                rentalRefundReason = rentalLikePaid > 0
                    ? "Hủy trước giờ nhận từ 24 đến dưới 48 giờ: hoàn 50% tiền thuê."
                    : "Chưa phát sinh tiền thuê đã thu.";
            }
            else
            {
                refundableRentalAmount = 0m;
                rentalRefundReason = rentalLikePaid > 0
                    ? "Hủy trước giờ nhận dưới 24 giờ: không hoàn tiền thuê."
                    : "Chưa phát sinh tiền thuê đã thu.";
            }
        }

        var refundAmount = refundableRentalAmount + depositPaid;
        var refundReason = depositPaid > 0
            ? $"{rentalRefundReason} Hoàn cọc {depositPaid:N0} đồng."
            : rentalRefundReason;

        if (rentalLikePaid <= 0 && depositPaid <= 0)
        {
            refundAmount = 0m;
            refundReason = "Đơn chưa có khoản đã thu nên không phát sinh hoàn tiền.";
        }

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

        var hasExistingRefund = booking.Payments.Any(payment => payment.Type == PaymentType.Refund);
        if (!hasExistingRefund)
        {
            if (refundableRentalAmount > 0)
            {
                booking.Payments.Add(new Payment
                {
                    Type = PaymentType.Refund,
                    Amount = refundableRentalAmount,
                    Method = PaymentMethods.BankTransferRefund,
                    Status = PaymentStatus.AwaitingRefund
                });
            }

            if (depositPaid > 0)
            {
                booking.Payments.Add(new Payment
                {
                    Type = PaymentType.Refund,
                    Amount = depositPaid,
                    Method = PaymentMethods.DepositRefund,
                    Status = PaymentStatus.AwaitingRefund
                });
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được hủy",
            Message = refundAmount > 0
                ? $"Đơn #{booking.BookingId} đã hủy. Tổng {refundAmount:N0} đồng đang chờ hoàn."
                : $"Đơn #{booking.BookingId} đã hủy và không có khoản hoàn."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            actorId,
            isAdmin ? "AdminCancel" : "CustomerCancel",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"Hủy đơn. Lý do: {booking.CancelReason}. Hoàn tiền thuê: {refundableRentalAmount:N0}; hoàn cọc: {depositPaid:N0}; tổng: {refundAmount:N0} đồng.",
            cancellationToken: cancellationToken);

        return RefundResult.Success(refundAmount);
    }
}
