using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class BookingReservationPolicy
{
    private static readonly BookingStatus[] BlockingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;

    public BookingReservationPolicy(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ExpireStaleReservationsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var candidates = await _dbContext.Bookings
            .Include(item => item.Payments)
            .Where(item =>
                item.Status == BookingStatus.PendingConfirmation ||
                item.Status == BookingStatus.PendingPayment)
            .ToListAsync(cancellationToken);

        var changed = false;

        foreach (var booking in candidates)
        {
            var bookingPolicy = booking.Policy;
            var transferAwaitingConfirmation =
                booking.Status == BookingStatus.PendingPayment &&
                booking.Payments.Any(payment =>
                    payment.Status == PaymentStatus.AwaitingConfirmation &&
                    payment.Type is PaymentType.Rental or PaymentType.Deposit or PaymentType.VehicleSwapAdjustment);

            if (!booking.ReservationExpiresAt.HasValue)
            {
                booking.ReservationExpiresAt = booking.Status == BookingStatus.PendingConfirmation
                    ? booking.CreatedAt.AddMinutes(bookingPolicy.BookingConfirmationHoldMinutes)
                    : now.AddMinutes(
                        transferAwaitingConfirmation
                            ? bookingPolicy.BookingTransferReconciliationHoldMinutes
                            : bookingPolicy.BookingPaymentHoldMinutes);
                changed = true;
            }

            if (booking.ReservationExpiresAt > now)
            {
                continue;
            }

            var oldStatus = booking.Status;
            var expiredDuringTransferReconciliation = transferAwaitingConfirmation;

            booking.Status = BookingStatus.Expired;
            booking.CancelledBy = "Hệ thống";
            booking.CancelledAt = now;
            booking.CancelReason = oldStatus == BookingStatus.PendingConfirmation
                ? $"Yêu cầu đặt xe hết hạn sau {bookingPolicy.BookingConfirmationHoldMinutes} phút chờ xác nhận."
                : expiredDuringTransferReconciliation
                    ? $"Đơn hết hạn sau {bookingPolicy.BookingTransferReconciliationHoldMinutes} phút chờ đối soát chuyển khoản."
                    : $"Đơn hết hạn sau {bookingPolicy.BookingPaymentHoldMinutes} phút chờ thanh toán.";
            booking.ReservationExpiresAt = null;

            // Booking hết hold phải giải phóng lịch xe, nhưng KHÔNG được tự cho rằng
            // tiền đang chờ đối soát là chưa vào tài khoản. Khoản AwaitingConfirmation được giữ
            // nguyên để Staff đối soát muộn; nếu xác nhận đã nhận tiền thì hệ thống tạo refund,
            // không hồi sinh booking đã Expired.
            foreach (var payment in booking.Payments.Where(payment =>
                         payment.Type is PaymentType.Rental or PaymentType.Deposit or PaymentType.VehicleSwapAdjustment &&
                         payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation))
            {
                if (BookingWorkflowRules.PreserveForLateReconciliationOnReservationExpiry(
                        payment.Status))
                {
                    continue;
                }

                payment.Status = PaymentStatus.Failed;
                payment.PaidAt = null;
                payment.TransactionCode = null;
            }

            var refundCreatedAtExpiry = 0m;
            if (oldStatus == BookingStatus.PendingPayment)
            {
                var grossRevenuePaid = booking.Payments
                    .Where(payment =>
                        payment.Status == PaymentStatus.Paid &&
                        payment.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
                    .Sum(payment => payment.Amount);
                var revenueRefundAlreadyPlanned = booking.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.Refund &&
                        payment.Method != PaymentMethods.DepositRefund &&
                        payment.Method != PaymentMethods.CompensationRefund &&
                        BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
                    .Sum(payment => payment.Amount);
                var revenueToRefund = BookingWorkflowRules.CalculateEffectivePaid(
                    grossRevenuePaid,
                    revenueRefundAlreadyPlanned);

                var grossDepositPaid = booking.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.Deposit &&
                        payment.Status == PaymentStatus.Paid)
                    .Sum(payment => payment.Amount);
                var depositRefundAlreadyPlanned = booking.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.Refund &&
                        payment.Method == PaymentMethods.DepositRefund &&
                        BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
                    .Sum(payment => payment.Amount);
                var depositToRefund = BookingWorkflowRules.CalculateEffectivePaid(
                    grossDepositPaid,
                    depositRefundAlreadyPlanned);

                if (revenueToRefund > 0m)
                {
                    booking.Payments.Add(new Payment
                    {
                        BookingId = booking.BookingId,
                        Type = PaymentType.Refund,
                        Amount = revenueToRefund,
                        Method = PaymentMethods.BankTransferRefund,
                        Status = PaymentStatus.AwaitingRefund
                    });
                }

                if (depositToRefund > 0m)
                {
                    booking.Payments.Add(new Payment
                    {
                        BookingId = booking.BookingId,
                        Type = PaymentType.Refund,
                        Amount = depositToRefund,
                        Method = PaymentMethods.DepositRefund,
                        Status = PaymentStatus.AwaitingRefund
                    });
                }

                refundCreatedAtExpiry = revenueToRefund + depositToRefund;
                if (refundCreatedAtExpiry > 0m)
                {
                    booking.RefundAmount = booking.Payments
                        .Where(payment =>
                            payment.Type == PaymentType.Refund &&
                            BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
                        .Sum(payment => payment.Amount);
                    booking.RefundReason = AppendText(
                        booking.RefundReason,
                        $"Đơn hết thời gian giữ chỗ trước khi bàn giao; hoàn toàn bộ {refundCreatedAtExpiry:N0} đồng đã thực thu còn lại.");
                }
            }

            var alreadyRecorded = await _dbContext.Set<BookingHoldEvent>()
                .AnyAsync(item => item.BookingId == booking.BookingId, cancellationToken);
            if (!alreadyRecorded)
            {
                _dbContext.Set<BookingHoldEvent>().Add(new BookingHoldEvent
                {
                    BookingId = booking.BookingId,
                    CustomerId = booking.CustomerId,
                    VehicleId = booking.VehicleId,
                    OccurredAt = now
                });
            }

            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Đơn giữ chỗ đã hết hạn",
                Message = oldStatus == BookingStatus.PendingConfirmation
                    ? $"Đơn #{booking.BookingId} đã hết thời gian chờ xác nhận và lịch xe đã được giải phóng."
                    : expiredDuringTransferReconciliation
                        ? $"Đơn #{booking.BookingId} đã quá thời gian đối soát chuyển khoản và lịch xe đã được giải phóng. Giao dịch bạn đã báo chuyển vẫn được giữ để Staff kiểm tra; nếu SmartCar xác nhận đã nhận tiền sau khi đơn hết hạn, khoản tiền đó sẽ được chuyển sang quy trình hoàn tiền thay vì khôi phục đơn."
                        : refundCreatedAtExpiry > 0m
                            ? $"Đơn #{booking.BookingId} đã hết thời gian thanh toán và lịch xe đã được giải phóng. SmartCar đã ghi nhận {refundCreatedAtExpiry:N0} đồng đã thu trước đó vào quy trình chờ duyệt hoàn tiền."
                            : $"Đơn #{booking.BookingId} đã hết thời gian thanh toán và lịch xe đã được giải phóng."
            });

            changed = true;
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> HasActiveUnpaidHoldAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return false;
        }

        return await _dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(booking =>
                booking.CustomerId == customerId &&
                (booking.Status == BookingStatus.PendingConfirmation ||
                 booking.Status == BookingStatus.PendingPayment),
                cancellationToken);
    }

    public async Task<BookingHoldRestriction> GetHoldRestrictionAsync(
        string customerId,
        int vehicleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return BookingHoldAbusePolicy.Evaluate(
                DateTime.UtcNow,
                Array.Empty<DateTime>(),
                Array.Empty<DateTime>());
        }

        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-BookingHoldAbusePolicy.RollingWindowHours);
        var events = await _dbContext.Set<BookingHoldEvent>()
            .AsNoTracking()
            .Where(item =>
                item.CustomerId == customerId &&
                !item.IsWaived &&
                item.OccurredAt >= windowStart &&
                item.OccurredAt <= now)
            .Select(item => new { item.VehicleId, item.OccurredAt })
            .ToListAsync(cancellationToken);

        return BookingHoldAbusePolicy.Evaluate(
            now,
            events.Select(item => item.OccurredAt).ToArray(),
            events.Where(item => item.VehicleId == vehicleId)
                .Select(item => item.OccurredAt)
                .ToArray());
    }

    public async Task SetConfirmationExpiryAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null || booking.Status != BookingStatus.PendingConfirmation)
        {
            return;
        }

        booking.ReservationExpiresAt = DateTime.UtcNow
            .AddMinutes(booking.Policy.BookingConfirmationHoldMinutes);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPaymentExpiryAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null || booking.Status != BookingStatus.PendingPayment)
        {
            return;
        }

        booking.ReservationExpiresAt = DateTime.UtcNow
            .AddMinutes(booking.Policy.BookingPaymentHoldMinutes);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetTransferReconciliationExpiryAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null || booking.Status != BookingStatus.PendingPayment)
        {
            return;
        }

        booking.ReservationExpiresAt = DateTime.UtcNow
            .AddMinutes(booking.Policy.BookingTransferReconciliationHoldMinutes);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> HasBufferedConflictAsync(
        int vehicleId,
        DateTime pickupDate,
        DateTime returnDate,
        VehiclePickupMethod pickupMethod,
        int? excludedBookingId,
        CancellationToken cancellationToken = default)
    {
        RentalPolicySnapshot requestedPolicy;
        if (excludedBookingId.HasValue)
        {
            var bookingPolicyRow = await _dbContext.Bookings
                .AsNoTracking()
                .Where(booking => booking.BookingId == excludedBookingId.Value)
                .Select(booking => new { booking.PolicyJson })
                .FirstOrDefaultAsync(cancellationToken);
            requestedPolicy = bookingPolicyRow is null
                ? await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken)
                : RentalPolicySnapshot.FromJson(bookingPolicyRow.PolicyJson);
        }
        else
        {
            requestedPolicy = await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken);
        }

        var candidates = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.VehicleId == vehicleId &&
                (!excludedBookingId.HasValue || booking.BookingId != excludedBookingId.Value) &&
                BlockingStatuses.Contains(booking.Status))
            .Select(booking => new
            {
                booking.PickupDate,
                booking.ReturnDate,
                booking.PickupMethod,
                booking.PolicyJson
            })
            .ToListAsync(cancellationToken);

        var requestedPreparation = TimeSpan.FromMinutes(
            requestedPolicy.GetOperationalPreparationMinutes(pickupMethod));

        foreach (var existing in candidates)
        {
            var existingPolicy = RentalPolicySnapshot.FromJson(existing.PolicyJson);
            var existingPreparation = TimeSpan.FromMinutes(
                existingPolicy.GetOperationalPreparationMinutes(existing.PickupMethod));

            if (pickupDate < existing.ReturnDate.Add(requestedPreparation) &&
                returnDate.Add(existingPreparation) > existing.PickupDate)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<DateTime?> GetActualTurnaroundBlockedUntilAsync(
        int vehicleId,
        DateTime pickupDate,
        VehiclePickupMethod pickupMethod,
        int? excludedBookingId = null,
        CancellationToken cancellationToken = default)
    {
        var vehicleStatus = await _dbContext.Vehicles
            .AsNoTracking()
            .Where(vehicle => vehicle.VehicleId == vehicleId)
            .Select(vehicle => (VehicleStatus?)vehicle.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (vehicleStatus != VehicleStatus.Inspection)
        {
            return null;
        }

        var latestReturnedAt = await _dbContext.VehicleReturns
            .AsNoTracking()
            .Where(vehicleReturn =>
                vehicleReturn.Booking.VehicleId == vehicleId &&
                (!excludedBookingId.HasValue || vehicleReturn.BookingId != excludedBookingId.Value) &&
                vehicleReturn.Booking.PickupDate < pickupDate)
            .OrderByDescending(vehicleReturn => vehicleReturn.ReturnedAt)
            .Select(vehicleReturn => (DateTime?)vehicleReturn.ReturnedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (!latestReturnedAt.HasValue)
        {
            return null;
        }

        RentalPolicySnapshot requestedPolicy;
        if (excludedBookingId.HasValue)
        {
            var bookingPolicyRow = await _dbContext.Bookings
                .AsNoTracking()
                .Where(booking => booking.BookingId == excludedBookingId.Value)
                .Select(booking => new { booking.PolicyJson })
                .FirstOrDefaultAsync(cancellationToken);
            requestedPolicy = bookingPolicyRow is null
                ? await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken)
                : RentalPolicySnapshot.FromJson(bookingPolicyRow.PolicyJson);
        }
        else
        {
            requestedPolicy = await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken);
        }

        return latestReturnedAt.Value.AddMinutes(
            requestedPolicy.GetOperationalPreparationMinutes(pickupMethod));
    }

    public async Task<decimal> GetOutstandingTrafficFineDebtAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return 0m;
        }

        return await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Booking.CustomerId == customerId &&
                payment.Type == PaymentType.TrafficFine &&
                (payment.Status == PaymentStatus.Pending ||
                 payment.Status == PaymentStatus.AwaitingConfirmation ||
                 payment.Status == PaymentStatus.Failed))
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken)
            ?? 0m;
    }

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";

}
