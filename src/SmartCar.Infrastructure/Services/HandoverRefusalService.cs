using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class HandoverRefusalService : IHandoverRefusalService
{
    private const decimal RentalRetentionRate = 0.40m;

    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public HandoverRefusalService(
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public async Task<HandoverRefusalPreviewDto?> GetPreviewAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Payments)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(
                item => item.BookingId == bookingId,
                cancellationToken);

        if (booking is null ||
            booking.Status != BookingStatus.ReadyForPickup ||
            booking.Handover is not null)
        {
            return null;
        }

        var amounts = CalculateAmounts(booking);
        return new HandoverRefusalPreviewDto(
            booking.BookingId,
            amounts.PaidRentalAmount,
            amounts.PaidDepositAmount,
            amounts.RentalPenaltyAmount,
            amounts.IncurredPickupDeliveryFee,
            amounts.RetainedAmount,
            amounts.RentalRefundAmount,
            amounts.TotalRefundAmount);
    }

    public async Task<RefundResult> CancelAsync(
        string adminId,
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(
                item => item.BookingId == bookingId,
                cancellationToken);

        if (booking is null)
        {
            return RefundResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.ReadyForPickup ||
            booking.Handover is not null)
        {
            return RefundResult.Failure(
                "Chỉ được áp dụng chính sách khách từ chối ký khi đơn đang Sẵn sàng giao xe và xe chưa được bàn giao.");
        }

        var hasAwaitingInitialPayment = booking.Payments.Any(payment =>
            payment.Type is PaymentType.Rental or PaymentType.Deposit &&
            payment.Status == PaymentStatus.AwaitingConfirmation);
        if (hasAwaitingInitialPayment)
        {
            return RefundResult.Failure(
                "Tiền thuê hoặc cọc vẫn đang chờ đối soát. Hãy xử lý giao dịch trước khi hủy do khách từ chối ký biên bản.");
        }

        var amounts = CalculateAmounts(booking);
        if (amounts.PaidRentalAmount <= 0 || amounts.PaidDepositAmount <= 0)
        {
            return RefundResult.Failure(
                "Không tìm thấy đầy đủ tiền thuê và cọc đã thanh toán để áp dụng chính sách từ chối ký biên bản.");
        }

        booking.Status = BookingStatus.Cancelled;
        booking.CancelledBy = "Khách hàng";
        booking.CancelledAt = DateTime.UtcNow;
        booking.CancelReason =
            "Khách từ chối ký biên bản bàn giao tại thời điểm nhận xe; SmartCar không giao xe và không giao chìa khóa.";
        booking.RefundAmount = amounts.TotalRefundAmount;
        booking.RefundReason =
            $"Khách từ chối ký biên bản bàn giao khi xe chưa được giao: " +
            $"giữ {RentalRetentionRate:P0} tiền thuê ({amounts.RentalPenaltyAmount:N0} đồng)" +
            (amounts.IncurredPickupDeliveryFee > 0
                ? $" và chi phí lượt giao xe đã phát sinh {amounts.IncurredPickupDeliveryFee:N0} đồng"
                : string.Empty) +
            $"; tổng giữ lại {amounts.RetainedAmount:N0} đồng; " +
            $"hoàn phần tiền chuyến {amounts.RentalRefundAmount:N0} đồng và hoàn 100% cọc {amounts.PaidDepositAmount:N0} đồng. " +
            $"Tổng hoàn {amounts.TotalRefundAmount:N0} đồng.";

        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

        EnsureRefundPayment(
            booking,
            PaymentType.Refund,
            amounts.RentalRefundAmount,
            PaymentMethods.BankTransferRefund);
        EnsureRefundPayment(
            booking,
            PaymentType.DepositRefund,
            amounts.PaidDepositAmount,
            RentalPolicyConstants.SecurityDepositRefundMethod);

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã hủy do không hoàn tất bàn giao",
            Message =
                $"Đơn #{booking.BookingId} đã được hủy vì bạn từ chối ký biên bản bàn giao nên xe chưa được giao. " +
                $"SmartCar giữ {amounts.RetainedAmount:N0} đồng theo chính sách hủy tại thời điểm bàn giao; " +
                $"hoàn phần tiền chuyến {amounts.RentalRefundAmount:N0} đồng và hoàn 100% cọc {amounts.PaidDepositAmount:N0} đồng. " +
                $"Tổng khoản hoàn là {amounts.TotalRefundAmount:N0} đồng."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "HandoverSignatureRefusalCancel",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"Khách từ chối ký biên bản bàn giao; xe và chìa khóa chưa được giao. " +
            $"Giữ {amounts.RetainedAmount:N0} đồng gồm 40% tiền thuê {amounts.RentalPenaltyAmount:N0} đồng" +
            (amounts.IncurredPickupDeliveryFee > 0
                ? $" và phí lượt giao đã phát sinh {amounts.IncurredPickupDeliveryFee:N0} đồng"
                : string.Empty) +
            $"; hoàn tiền chuyến {amounts.RentalRefundAmount:N0} đồng; hoàn cọc {amounts.PaidDepositAmount:N0} đồng; " +
            $"tổng hoàn {amounts.TotalRefundAmount:N0} đồng.",
            cancellationToken: cancellationToken);

        return RefundResult.Success(amounts.TotalRefundAmount);
    }

    private static RefusalAmounts CalculateAmounts(Booking booking)
    {
        var paidRentalAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Rental &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
        var paidDepositAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var rentalPenaltyAmount = Math.Round(
            booking.RentalAmount * RentalRetentionRate,
            0,
            MidpointRounding.AwayFromZero);
        var incurredPickupDeliveryFee =
            booking.PickupMethod == DeliveryConstants.HomeDelivery
                ? Math.Round(
                    booking.PickupDeliveryDistanceKm * booking.DeliveryRatePerKm,
                    0,
                    MidpointRounding.AwayFromZero)
                : 0;
        var retainedAmount = Math.Min(
            paidRentalAmount,
            Math.Max(0, rentalPenaltyAmount + incurredPickupDeliveryFee));
        var rentalRefundAmount = Math.Max(0, paidRentalAmount - retainedAmount);
        var totalRefundAmount = rentalRefundAmount + paidDepositAmount;

        return new RefusalAmounts(
            paidRentalAmount,
            paidDepositAmount,
            rentalPenaltyAmount,
            incurredPickupDeliveryFee,
            retainedAmount,
            rentalRefundAmount,
            totalRefundAmount);
    }

    private static void EnsureRefundPayment(
        Booking booking,
        PaymentType type,
        decimal amount,
        string method)
    {
        if (amount <= 0)
        {
            return;
        }

        var payment = booking.Payments.FirstOrDefault(item => item.Type == type);
        if (payment is null)
        {
            booking.Payments.Add(new Payment
            {
                Type = type,
                Amount = amount,
                Method = method,
                Status = PaymentStatus.AwaitingRefund,
                PaidAt = null,
                TransactionCode = null
            });
            return;
        }

        if (payment.Status == PaymentStatus.AwaitingRefund)
        {
            payment.Amount = amount;
            payment.Method = method;
        }
    }

    private sealed record RefusalAmounts(
        decimal PaidRentalAmount,
        decimal PaidDepositAmount,
        decimal RentalPenaltyAmount,
        decimal IncurredPickupDeliveryFee,
        decimal RetainedAmount,
        decimal RentalRefundAmount,
        decimal TotalRefundAmount);
}
