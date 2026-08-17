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

        var depositRefundAlreadyPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);

        var remainingDeposit = Math.Max(0m, depositPaid - depositRefundAlreadyPlanned);
        var existingRefundTotal = booking.Payments
            .Where(payment => payment.Type == PaymentType.Refund)
            .Sum(payment => payment.Amount);

        booking.Status = BookingStatus.NoShow;
        booking.NoShowMarkedAt = DateTime.UtcNow;
        booking.CancelledBy = "Quản trị viên";
        booking.CancelReason = "Khách không đến nhận xe đúng thời gian quy định.";
        booking.RefundAmount = existingRefundTotal + remainingDeposit;
        booking.RefundReason = AppendText(
            booking.RefundReason,
            remainingDeposit > 0
                ? $"Không hoàn tiền thuê. Hoàn phần cọc còn lại {remainingDeposit:N0} đồng."
                : "Không hoàn tiền thuê; không còn cọc cần hoàn.");

        if (remainingDeposit > 0)
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = remainingDeposit,
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
            Message = remainingDeposit > 0
                ? $"Đơn #{booking.BookingId}: tiền thuê không hoàn; cọc còn lại {remainingDeposit:N0} đồng đang chờ hoàn."
                : $"Đơn #{booking.BookingId}: tiền thuê không hoàn."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "MarkNoShow",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"Khách không đến nhận. Không hoàn tiền thuê; hoàn cọc còn lại {remainingDeposit:N0} đồng.",
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

        var grossRentalPaid = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type is PaymentType.Rental or PaymentType.Extension or PaymentType.VehicleSwapAdjustment)
            .Sum(payment => payment.Amount);

        var revenueRefundAlreadyPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method != PaymentMethods.DepositRefund &&
                payment.Method != PaymentMethods.CompensationRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);

        var rentalPaid = Math.Max(0m, grossRentalPaid - revenueRefundAlreadyPlanned);

        var grossDepositPaid = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type == PaymentType.Deposit)
            .Sum(payment => payment.Amount);

        var depositRefundAlreadyPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);

        var depositToRefund = Math.Max(0m, grossDepositPaid - depositRefundAlreadyPlanned);

        decimal refundableRentalAmount;
        string rentalRefundReason;

        if (isAdmin)
        {
            refundableRentalAmount = rentalPaid;
            rentalRefundReason = rentalPaid > 0
                ? "SmartCar chủ động hủy trước khi giao xe: hoàn 100% tiền thuê còn lại đã thu."
                : "Không còn tiền thuê cần hoàn.";
        }
        else
        {
            var hoursBeforePickup = (booking.PickupDate - DateTime.Now).TotalHours;

            if (hoursBeforePickup >= 48)
            {
                refundableRentalAmount = rentalPaid;
                rentalRefundReason = rentalPaid > 0
                    ? "Hủy trước giờ nhận từ 48 giờ: hoàn 100% tiền thuê."
                    : "Không còn tiền thuê cần hoàn.";
            }
            else if (hoursBeforePickup >= 24)
            {
                refundableRentalAmount = Math.Round(rentalPaid * 0.5m, 0);
                rentalRefundReason = rentalPaid > 0
                    ? "Hủy trước giờ nhận từ 24 đến dưới 48 giờ: hoàn 50% tiền thuê."
                    : "Không còn tiền thuê cần hoàn.";
            }
            else
            {
                refundableRentalAmount = 0m;
                rentalRefundReason = rentalPaid > 0
                    ? "Hủy trước giờ nhận dưới 24 giờ: không hoàn tiền thuê."
                    : "Không còn tiền thuê cần hoàn.";
            }
        }

        var newRefundAmount = refundableRentalAmount + depositToRefund;
        var existingRefundTotal = booking.Payments
            .Where(payment => payment.Type == PaymentType.Refund)
            .Sum(payment => payment.Amount);

        booking.Status = BookingStatus.Cancelled;
        booking.CancelReason = request.Reason.Trim();
        booking.CancelledBy = isAdmin ? "Quản trị viên" : "Khách hàng";
        booking.CancelledAt = DateTime.UtcNow;
        booking.RefundAmount = existingRefundTotal + newRefundAmount;
        booking.RefundReason = AppendText(
            booking.RefundReason,
            depositToRefund > 0
                ? $"{rentalRefundReason} Hoàn phần cọc còn lại {depositToRefund:N0} đồng."
                : rentalRefundReason);

        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

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

        if (depositToRefund > 0)
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = depositToRefund,
                Method = PaymentMethods.DepositRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được hủy",
            Message = newRefundAmount > 0
                ? $"Đơn #{booking.BookingId} đã hủy. Có thêm {newRefundAmount:N0} đồng đang chờ hoàn."
                : $"Đơn #{booking.BookingId} đã hủy và không phát sinh khoản hoàn mới."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            actorId,
            isAdmin ? "AdminCancel" : "CustomerCancel",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"Hủy đơn. Lý do: {booking.CancelReason}. Hoàn tiền thuê mới: {refundableRentalAmount:N0}; hoàn cọc mới: {depositToRefund:N0}; tổng mới: {newRefundAmount:N0} đồng.",
            cancellationToken: cancellationToken);

        return RefundResult.Success(newRefundAmount);
    }

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";
}
