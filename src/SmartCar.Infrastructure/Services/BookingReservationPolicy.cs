using Microsoft.EntityFrameworkCore;
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
            if (booking.Status == BookingStatus.PendingPayment &&
                booking.Payments.Any(payment =>
                    payment.Type == PaymentType.Rental &&
                    payment.Status == PaymentStatus.AwaitingConfirmation))
            {
                // Khách đã báo chuyển khoản đúng lúc: không tự hết hạn khi SmartCar còn đang đối soát.
                if (booking.ReservationExpiresAt.HasValue)
                {
                    booking.ReservationExpiresAt = null;
                    changed = true;
                }

                continue;
            }

            if (!booking.ReservationExpiresAt.HasValue)
            {
                // Tương thích các đơn cũ tạo trước khi có cột ReservationExpiresAt.
                booking.ReservationExpiresAt = booking.Status == BookingStatus.PendingConfirmation
                    ? booking.CreatedAt.AddMinutes(RentalPolicy.BookingConfirmationHoldMinutes)
                    : booking.CreatedAt.AddMinutes(
                        RentalPolicy.BookingConfirmationHoldMinutes +
                        RentalPolicy.BookingPaymentHoldMinutes);
                changed = true;
            }

            if (booking.ReservationExpiresAt > now)
            {
                continue;
            }

            var oldStatus = booking.Status;
            booking.Status = BookingStatus.Expired;
            booking.CancelledBy = "Hệ thống";
            booking.CancelledAt = now;
            booking.CancelReason = oldStatus == BookingStatus.PendingConfirmation
                ? $"Yêu cầu đặt xe hết hạn sau {RentalPolicy.BookingConfirmationHoldMinutes} phút chờ xác nhận."
                : $"Đơn hết hạn sau {RentalPolicy.BookingPaymentHoldMinutes} phút chờ thanh toán.";
            booking.ReservationExpiresAt = null;

            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Đơn giữ chỗ đã hết hạn",
                Message = oldStatus == BookingStatus.PendingConfirmation
                    ? $"Đơn #{booking.BookingId} đã hết thời gian chờ xác nhận và lịch xe đã được giải phóng."
                    : $"Đơn #{booking.BookingId} đã hết thời gian thanh toán và lịch xe đã được giải phóng."
            });

            changed = true;
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
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

    public Task<bool> HasBufferedConflictAsync(
        int vehicleId,
        DateTime pickupDate,
        DateTime returnDate,
        int? excludedBookingId,
        CancellationToken cancellationToken = default)
    {
        var bufferMinutes = RentalPolicy.VehicleTurnaroundMinutes;

        return _dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(booking =>
                booking.VehicleId == vehicleId &&
                (!excludedBookingId.HasValue || booking.BookingId != excludedBookingId.Value) &&
                BlockingStatuses.Contains(booking.Status) &&
                pickupDate < booking.ReturnDate.AddMinutes(bufferMinutes) &&
                returnDate.AddMinutes(bufferMinutes) > booking.PickupDate,
                cancellationToken);
    }

    public async Task<DateTime?> GetActualTurnaroundBlockedUntilAsync(
        int vehicleId,
        DateTime pickupDate,
        int? excludedBookingId = null,
        CancellationToken cancellationToken = default)
    {
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
