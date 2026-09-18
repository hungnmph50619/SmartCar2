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
            var transferAwaitingConfirmation =
                booking.Status == BookingStatus.PendingPayment &&
                booking.Payments.Any(payment =>
                    payment.Status == PaymentStatus.AwaitingConfirmation &&
                    payment.Type is PaymentType.Rental or PaymentType.Deposit or PaymentType.VehicleSwapAdjustment);

            if (!booking.ReservationExpiresAt.HasValue)
            {
                booking.ReservationExpiresAt = booking.Status == BookingStatus.PendingConfirmation
                    ? booking.CreatedAt.AddMinutes(RentalPolicy.BookingConfirmationHoldMinutes)
                    : now.AddMinutes(
                        transferAwaitingConfirmation
                            ? RentalPolicy.BookingTransferReconciliationHoldMinutes
                            : RentalPolicy.BookingPaymentHoldMinutes);
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
                ? $"Yêu cầu đặt xe hết hạn sau {RentalPolicy.BookingConfirmationHoldMinutes} phút chờ xác nhận."
                : expiredDuringTransferReconciliation
                    ? $"Đơn hết hạn sau {RentalPolicy.BookingTransferReconciliationHoldMinutes} phút chờ đối soát chuyển khoản."
                    : $"Đơn hết hạn sau {RentalPolicy.BookingPaymentHoldMinutes} phút chờ thanh toán.";
            booking.ReservationExpiresAt = null;

            // Một booking đã hết hold không được để payment Pending/AwaitingConfirmation tiếp tục
            // giữ trạng thái mập mờ. Chỉ đóng các khoản tiền thuê/cọc chưa thanh toán của hold này.
            foreach (var payment in booking.Payments.Where(payment =>
                         payment.Type is PaymentType.Rental or PaymentType.Deposit or PaymentType.VehicleSwapAdjustment &&
                         payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation))
            {
                payment.Status = PaymentStatus.Failed;
                payment.PaidAt = null;
                payment.TransactionCode = null;
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
                        ? $"Đơn #{booking.BookingId} đã quá thời gian đối soát chuyển khoản và lịch xe đã được giải phóng. Nếu bạn thực sự đã chuyển tiền, vui lòng liên hệ SmartCar để kiểm tra giao dịch."
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
            .AddMinutes(RentalPolicy.BookingConfirmationHoldMinutes);
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
            .AddMinutes(RentalPolicy.BookingPaymentHoldMinutes);
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
            .AddMinutes(RentalPolicy.BookingTransferReconciliationHoldMinutes);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> HasBufferedConflictAsync(
        int vehicleId,
        DateTime pickupDate,
        DateTime returnDate,
        VehiclePickupMethod pickupMethod,
        int? excludedBookingId,
        CancellationToken cancellationToken = default)
    {
        var turnaroundMinutes = RentalPolicy.VehicleTurnaroundMinutes;
        var requestedLeadMinutes = pickupMethod == VehiclePickupMethod.Delivery
            ? RentalPolicy.DeliveryLeadMinutes
            : 0;

        return _dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(booking =>
                booking.VehicleId == vehicleId &&
                (!excludedBookingId.HasValue || booking.BookingId != excludedBookingId.Value) &&
                BlockingStatuses.Contains(booking.Status) &&
                pickupDate < booking.ReturnDate.AddMinutes(turnaroundMinutes + requestedLeadMinutes) &&
                returnDate.AddMinutes(
                    turnaroundMinutes +
                    (booking.PickupMethod == VehiclePickupMethod.Delivery
                        ? RentalPolicy.DeliveryLeadMinutes
                        : 0)) > booking.PickupDate,
                cancellationToken);
    }

    public async Task<DateTime?> GetActualTurnaroundBlockedUntilAsync(
        int vehicleId,
        DateTime pickupDate,
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

        return latestReturnedAt?.AddMinutes(RentalPolicy.VehicleTurnaroundMinutes);
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
}
