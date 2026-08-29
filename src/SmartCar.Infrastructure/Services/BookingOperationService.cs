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

    public Task<RefundResult> CancelByCustomerAsync(string customerId, CancelBookingRequest request, CancellationToken cancellationToken = default) =>
        CancelAsync(customerId, false, request, cancellationToken);

    public Task<RefundResult> CancelByAdminAsync(string adminId, CancelBookingRequest request, CancellationToken cancellationToken = default) =>
        CancelAsync(adminId, true, request, cancellationToken);

    public async Task<OperationResult> MarkNoShowAsync(int bookingId, string adminId, bool customerContacted, CancellationToken cancellationToken = default)
    {
        if (!customerContacted)
            return OperationResult.Failure("Hãy xác nhận đã liên hệ khách trước khi ghi nhận không đến nhận xe.");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
            return OperationResult.Failure("Không tìm thấy đơn thuê.");

        if (booking.Status != BookingStatus.ReadyForPickup)
            return OperationResult.Failure("Chỉ được ghi nhận khách không đến sau khi SmartCar đã xác nhận xe thực sự sẵn sàng giao. Đơn đang chờ xe hoặc mới thanh toán không được tính no-show.");

        if (booking.Handover is not null)
            return OperationResult.Failure("Đơn đã có biên bản giao xe điện tử nên không thể ghi nhận khách không đến nhận.");

        if (DateTime.Now < booking.PickupDate.AddMinutes(RentalPolicy.NoShowGraceMinutes))
            return OperationResult.Failure($"Chỉ được ghi nhận không đến sau giờ nhận ít nhất {RentalPolicy.NoShowGraceMinutes} phút.");

        var grossRevenuePaid = booking.Payments.Where(payment => payment.Status == PaymentStatus.Paid && payment.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment).Sum(payment => payment.Amount);
        var revenueRefundAlreadyPlanned = booking.Payments.Where(payment => payment.Type == PaymentType.Refund && payment.Method != PaymentMethods.DepositRefund && payment.Method != PaymentMethods.CompensationRefund && payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded).Sum(payment => payment.Amount);
        var remainingRevenuePaid = Math.Max(0m, grossRevenuePaid - revenueRefundAlreadyPlanned);
        var deliveryFee = Math.Max(0m, booking.TotalAmount - booking.RentalAmount - booking.AdditionalAmount);
        var deliveryPaid = Math.Min(deliveryFee, remainingRevenuePaid);
        var rentalPaid = Math.Max(0m, remainingRevenuePaid - deliveryPaid);
        var noShowFee = Math.Round(rentalPaid * RentalPolicy.NoShowFeeRate, 0, MidpointRounding.AwayFromZero);
        var refundableRental = Math.Max(0m, rentalPaid - noShowFee);
        var revenueRefund = refundableRental + deliveryPaid;
        var depositPaid = booking.Payments.Where(payment => payment.Status == PaymentStatus.Paid && payment.Type == PaymentType.Deposit).Sum(payment => payment.Amount);
        var depositRefundAlreadyPlanned = booking.Payments.Where(payment => payment.Type == PaymentType.Refund && payment.Method == PaymentMethods.DepositRefund && payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded).Sum(payment => payment.Amount);
        var remainingDeposit = Math.Max(0m, depositPaid - depositRefundAlreadyPlanned);
        var newRefundAmount = revenueRefund + remainingDeposit;
        var existingRefundTotal = booking.Payments.Where(payment => payment.Type == PaymentType.Refund).Sum(payment => payment.Amount);

        booking.Status = BookingStatus.NoShow;
        booking.NoShowMarkedAt = DateTime.UtcNow;
        booking.CancelledBy = "Quản trị viên";
        booking.CancelReason = $"Khách không đến nhận xe sau {RentalPolicy.NoShowGraceMinutes} phút; xe đã ở trạng thái sẵn sàng giao và quản trị viên xác nhận đã liên hệ khách.";
        booking.RefundAmount = existingRefundTotal + newRefundAmount;
        booking.RefundReason = AppendText(booking.RefundReason,
            $"Phí không đến nhận {RentalPolicy.NoShowFeeRate:P0} tiền thuê: {noShowFee:N0} đồng. Hoàn phần tiền thuê còn lại {refundableRental:N0} đồng" +
            (deliveryPaid > 0 ? $", hoàn phí giao chưa thực hiện {deliveryPaid:N0} đồng" : string.Empty) + $"; hoàn cọc còn lại {remainingDeposit:N0} đồng.");

        if (revenueRefund > 0)
            booking.Payments.Add(new Payment { Type = PaymentType.Refund, Amount = revenueRefund, Method = PaymentMethods.BankTransferRefund, Status = PaymentStatus.AwaitingRefund });
        if (remainingDeposit > 0)
            booking.Payments.Add(new Payment { Type = PaymentType.Refund, Amount = remainingDeposit, Method = PaymentMethods.DepositRefund, Status = PaymentStatus.AwaitingRefund });

        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(_dbContext, booking.Vehicle, cancellationToken: cancellationToken);
        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Không đến nhận xe",
            Message = $"Đơn #{booking.BookingId}: phí không đến nhận {noShowFee:N0} đồng; tổng tiền thuê/phí giao được hoàn {revenueRefund:N0} đồng và cọc được hoàn {remainingDeposit:N0} đồng."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _auditService.WriteAsync(adminId, "MarkNoShow", nameof(Booking), booking.BookingId.ToString(), $"Khách không đến sau khi xe đã sẵn sàng; đã liên hệ khách. Phí {noShowFee:N0}; hoàn thuê/phí giao {revenueRefund:N0}; hoàn cọc {remainingDeposit:N0} đồng.", cancellationToken: cancellationToken);
        return OperationResult.Success();
    }

    private async Task<RefundResult> CancelAsync(string actorId, bool isAdmin, CancelBookingRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return RefundResult.Failure("Vui lòng nhập lý do hủy đơn.");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var query = _dbContext.Bookings.Include(item => item.Vehicle).Include(item => item.Payments).AsQueryable();
        if (!isAdmin) query = query.Where(item => item.CustomerId == actorId);
        var booking = await query.FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);
        if (booking is null) return RefundResult.Failure("Không tìm thấy đơn thuê.");
        if (booking.Status is BookingStatus.Rented or BookingStatus.PendingInspection or BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.NoShow or BookingStatus.Rejected)
            return RefundResult.Failure("Trạng thái hiện tại không cho phép hủy đơn.");

        var grossRevenuePaid = booking.Payments.Where(payment => payment.Status == PaymentStatus.Paid && payment.Type is PaymentType.Rental or PaymentType.Extension or PaymentType.VehicleSwapAdjustment).Sum(payment => payment.Amount);
        var revenueRefundAlreadyPlanned = booking.Payments.Where(payment => payment.Type == PaymentType.Refund && payment.Method != PaymentMethods.DepositRefund && payment.Method != PaymentMethods.CompensationRefund && payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded).Sum(payment => payment.Amount);
        var revenuePaid = Math.Max(0m, grossRevenuePaid - revenueRefundAlreadyPlanned);
        var deliveryFee = Math.Max(0m, booking.TotalAmount - booking.RentalAmount - booking.AdditionalAmount);
        var deliveryPaid = Math.Min(deliveryFee, revenuePaid);
        var rentalPaid = Math.Max(0m, revenuePaid - deliveryPaid);
        var grossDepositPaid = booking.Payments.Where(payment => payment.Status == PaymentStatus.Paid && payment.Type == PaymentType.Deposit).Sum(payment => payment.Amount);
        var depositRefundAlreadyPlanned = booking.Payments.Where(payment => payment.Type == PaymentType.Refund && payment.Method == PaymentMethods.DepositRefund && payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded).Sum(payment => payment.Amount);
        var depositToRefund = Math.Max(0m, grossDepositPaid - depositRefundAlreadyPlanned);

        decimal refundableRevenueAmount;
        string rentalRefundReason;
        if (isAdmin)
        {
            refundableRevenueAmount = revenuePaid;
            rentalRefundReason = revenuePaid > 0 ? "SmartCar chủ động hủy trước khi giao xe: hoàn 100% tiền thuê và phí giao còn lại đã thu." : "Không còn tiền thuê hoặc phí giao cần hoàn.";
        }
        else
        {
            var hoursBeforePickup = (booking.PickupDate - DateTime.Now).TotalHours;
            if (hoursBeforePickup >= 48)
            {
                refundableRevenueAmount = rentalPaid + deliveryPaid;
                rentalRefundReason = revenuePaid > 0 ? "Hủy trước giờ nhận từ 48 giờ: hoàn 100% tiền thuê và phí giao chưa thực hiện." : "Không còn tiền thuê hoặc phí giao cần hoàn.";
            }
            else if (hoursBeforePickup >= 24)
            {
                var refundableRental = Math.Round(rentalPaid * 0.5m, 0, MidpointRounding.AwayFromZero);
                refundableRevenueAmount = refundableRental + deliveryPaid;
                rentalRefundReason = revenuePaid > 0 ? "Hủy trước giờ nhận từ 24 đến dưới 48 giờ: hoàn 50% tiền thuê và 100% phí giao chưa thực hiện." : "Không còn tiền thuê hoặc phí giao cần hoàn.";
            }
            else
            {
                refundableRevenueAmount = deliveryPaid;
                rentalRefundReason = revenuePaid > 0 ? (deliveryPaid > 0 ? "Hủy trước giờ nhận dưới 24 giờ: không hoàn tiền thuê; hoàn 100% phí giao chưa thực hiện." : "Hủy trước giờ nhận dưới 24 giờ: không hoàn tiền thuê.") : "Không còn tiền thuê hoặc phí giao cần hoàn.";
            }
        }

        var newRefundAmount = refundableRevenueAmount + depositToRefund;
        var existingRefundTotal = booking.Payments.Where(payment => payment.Type == PaymentType.Refund).Sum(payment => payment.Amount);
        booking.Status = BookingStatus.Cancelled;
        booking.CancelReason = request.Reason.Trim();
        booking.CancelledBy = isAdmin ? "Quản trị viên" : "Khách hàng";
        booking.CancelledAt = DateTime.UtcNow;
        booking.RefundAmount = existingRefundTotal + newRefundAmount;
        booking.RefundReason = AppendText(booking.RefundReason, depositToRefund > 0 ? $"{rentalRefundReason} Hoàn phần cọc còn lại {depositToRefund:N0} đồng." : rentalRefundReason);
        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(_dbContext, booking.Vehicle, cancellationToken: cancellationToken);

        if (refundableRevenueAmount > 0)
            booking.Payments.Add(new Payment { Type = PaymentType.Refund, Amount = refundableRevenueAmount, Method = PaymentMethods.BankTransferRefund, Status = PaymentStatus.AwaitingRefund });
        if (depositToRefund > 0)
            booking.Payments.Add(new Payment { Type = PaymentType.Refund, Amount = depositToRefund, Method = PaymentMethods.DepositRefund, Status = PaymentStatus.AwaitingRefund });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được hủy",
            Message = newRefundAmount > 0 ? $"Đơn #{booking.BookingId} đã hủy. Có thêm {newRefundAmount:N0} đồng đang chờ hoàn." : $"Đơn #{booking.BookingId} đã hủy và không phát sinh khoản hoàn mới."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _auditService.WriteAsync(actorId, isAdmin ? "AdminCancel" : "CustomerCancel", nameof(Booking), booking.BookingId.ToString(), $"Hủy đơn. Lý do: {booking.CancelReason}. Hoàn tiền thuê/phí giao mới: {refundableRevenueAmount:N0}; hoàn cọc mới: {depositToRefund:N0}; tổng mới: {newRefundAmount:N0} đồng.", cancellationToken: cancellationToken);
        return RefundResult.Success(newRefundAmount);
    }

    private static string AppendText(string? current, string addition) => string.IsNullOrWhiteSpace(current) ? addition : $"{current.Trim()} {addition}";
}