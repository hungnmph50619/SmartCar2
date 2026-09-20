using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class PolicyAwareBookingService : IBookingService
{
    private readonly BookingService _inner;
    private readonly BookingReservationPolicy _policy;
    private readonly ApplicationDbContext _dbContext;

    public PolicyAwareBookingService(
        BookingService inner,
        BookingReservationPolicy policy,
        ApplicationDbContext dbContext)
    {
        _inner = inner;
        _policy = policy;
        _dbContext = dbContext;
    }

    public async Task<BookingMutationResult> CreateAsync(
        string customerId,
        CreateBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);
        var activePolicy = await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken);

        if (await _policy.HasActiveUnpaidHoldAsync(customerId, cancellationToken))
        {
            return BookingMutationResult.Failure(
                "Bạn đang có một đơn chưa thanh toán vẫn giữ lịch xe. Hãy hoàn tất hoặc chờ đơn đó hết hạn trước khi tạo thêm đơn giữ chỗ.");
        }

        var holdRestriction = await _policy.GetHoldRestrictionAsync(
            customerId,
            request.VehicleId,
            cancellationToken);
        var utcNow = DateTime.UtcNow;

        if (holdRestriction.IsAccountBlocked(utcNow))
        {
            var remaining = holdRestriction.AccountBlockedUntil!.Value - utcNow;
            return BookingMutationResult.Failure(
                $"Tài khoản tạm thời không được tạo giữ chỗ mới do có {holdRestriction.TimeoutCount24Hours} lần để đơn tự hết hạn trong 24 giờ gần nhất. " +
                $"Vui lòng thử lại sau khoảng {FormatRemaining(remaining)} hoặc liên hệ SmartCar nếu cần Admin kiểm tra và gỡ hạn chế có lý do.");
        }

        if (holdRestriction.IsVehicleCoolingDown(utcNow))
        {
            var remaining = holdRestriction.SameVehicleCooldownUntil!.Value - utcNow;
            return BookingMutationResult.Failure(
                $"Xe này đang trong thời gian chờ sau lần giữ chỗ hết hạn của tài khoản. Vui lòng thử lại sau khoảng {FormatRemaining(remaining)} hoặc chọn xe khác.");
        }

        var trafficFineDebt = await _policy.GetOutstandingTrafficFineDebtAsync(
            customerId,
            cancellationToken);

        if (trafficFineDebt > 0)
        {
            return BookingMutationResult.Failure(
                $"Tài khoản còn {trafficFineDebt:N0} đồng nghĩa vụ phạt/vi phạm chưa thanh toán. " +
                "Vui lòng xử lý khoản này trước khi tạo chuyến thuê mới.");
        }

        if (await _policy.HasBufferedConflictAsync(
                request.VehicleId,
                request.PickupDate,
                request.ReturnDate,
                request.PickupMethod,
                null,
                cancellationToken))
        {
            var deliveryLead = request.PickupMethod == VehiclePickupMethod.Delivery
                ? $" và thêm {activePolicy.DeliveryLeadMinutes} phút chuẩn bị giao tận nơi"
                : string.Empty;

            return BookingMutationResult.Failure(
                $"Xe không đủ khoảng vận hành giữa hai lượt thuê. SmartCar cần tối thiểu " +
                $"{activePolicy.VehicleTurnaroundMinutes} phút để nhận xe, kiểm tra và chuẩn bị lại{deliveryLead}.");
        }

        var blockedUntil = await _policy.GetActualTurnaroundBlockedUntilAsync(
            request.VehicleId,
            request.PickupDate,
            null,
            cancellationToken);

        if (blockedUntil.HasValue && blockedUntil.Value > request.PickupDate)
        {
            return BookingMutationResult.Failure(
                $"Xe vừa được trả thực tế và cần thời gian xoay vòng. Thời điểm nhận sớm nhất hiện tại là " +
                $"{blockedUntil.Value:dd/MM/yyyy HH:mm}.");
        }



        var result = await _inner.CreateAsync(customerId, request, cancellationToken);
        if (!result.Succeeded || !result.BookingId.HasValue)
        {
            return result;
        }

        var createdBooking = await _dbContext.Bookings
            .FirstOrDefaultAsync(
                booking => booking.BookingId == result.BookingId.Value,
                cancellationToken);

        if (createdBooking is null)
        {
            return BookingMutationResult.Failure(
                "Đơn đã được tạo nhưng không thể tải lại để áp dụng chính sách giữ cọc.");
        }


        var postConflict = await _policy.HasBufferedConflictAsync(
            request.VehicleId,
            request.PickupDate,
            request.ReturnDate,
            request.PickupMethod,
            result.BookingId.Value,
            cancellationToken);

        if (postConflict)
        {
            createdBooking.Status = BookingStatus.Expired;
            createdBooking.CancelledBy = "Hệ thống";
            createdBooking.CancelledAt = DateTime.UtcNow;
            createdBooking.CancelReason =
                "Lịch xe phát sinh xung đột trong lúc tạo đơn; hệ thống tự giải phóng giữ chỗ.";
            createdBooking.ReservationExpiresAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Đây là lỗi cạnh tranh lịch phát sinh trong lúc tạo, không phải hành vi khách bỏ hold,
            // nên không ghi BookingHoldEvent và không phạt cooldown.
            return BookingMutationResult.Failure(
                "Xe vừa có lịch thuê khác và không còn đủ thời gian xoay vòng. Vui lòng chọn khung giờ hoặc xe khác.");
        }

        await _policy.SetConfirmationExpiryAsync(result.BookingId.Value, cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<BookingListItemDto>> GetCustomerBookingsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);
        return await _inner.GetCustomerBookingsAsync(customerId, cancellationToken);
    }

    public async Task<BookingDetailsDto?> GetCustomerBookingAsync(
        int bookingId,
        string customerId,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);
        return await _inner.GetCustomerBookingAsync(bookingId, customerId, cancellationToken);
    }

    public async Task<IReadOnlyList<BookingListItemDto>> GetAdminBookingsAsync(
        BookingStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);
        return await _inner.GetAdminBookingsAsync(status, cancellationToken);
    }

    public async Task<BookingDetailsDto?> GetAdminBookingAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);
        return await _inner.GetAdminBookingAsync(bookingId, cancellationToken);
    }

    public async Task<OperationResult> ConfirmAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status == BookingStatus.Expired)
        {
            return OperationResult.Failure("Đơn đã hết thời gian giữ chỗ và lịch xe đã được giải phóng.");
        }

        if (!booking.StaffReviewedAt.HasValue ||
            string.IsNullOrWhiteSpace(booking.StaffReviewedByStaffId))
        {
            return OperationResult.Failure(
                "Đơn chưa hoàn tất bước Staff kiểm tra và gửi duyệt. Admin không được bỏ qua bước vận hành này.");
        }

        if (await _policy.HasBufferedConflictAsync(
                booking.VehicleId,
                booking.PickupDate,
                booking.ReturnDate,
                booking.PickupMethod,
                booking.BookingId,
                cancellationToken))
        {
            var bookingPolicy = booking.Policy;
            var deliveryLead = booking.PickupMethod == VehiclePickupMethod.Delivery
                ? $" và thêm {bookingPolicy.DeliveryLeadMinutes} phút chuẩn bị giao tận nơi"
                : string.Empty;

            return OperationResult.Failure(
                $"Không thể duyệt vì lịch xe không còn đủ {bookingPolicy.VehicleTurnaroundMinutes} phút xoay vòng{deliveryLead}.");
        }

        var blockedUntil = await _policy.GetActualTurnaroundBlockedUntilAsync(
            booking.VehicleId,
            booking.PickupDate,
            booking.BookingId,
            cancellationToken);

        if (blockedUntil.HasValue && blockedUntil.Value > booking.PickupDate)
        {
            return OperationResult.Failure(
                $"Xe chưa đủ thời gian chuẩn bị sau lượt trả thực tế. Thời điểm nhận sớm nhất là {blockedUntil.Value:dd/MM/yyyy HH:mm}.");
        }

        var result = await _inner.ConfirmAsync(bookingId, cancellationToken);
        if (result.Succeeded)
        {
            await _policy.SetPaymentExpiryAsync(bookingId, cancellationToken);
        }

        return result;
    }

    public Task<OperationResult> RejectAsync(
        int bookingId,
        string reason,
        CancellationToken cancellationToken = default) =>
        _inner.RejectAsync(bookingId, reason, cancellationToken);

    public async Task<OperationResult> MarkReadyForPickupAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        await _policy.ExpireStaleReservationsAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        var blockedUntil = await _policy.GetActualTurnaroundBlockedUntilAsync(
            booking.VehicleId,
            booking.PickupDate,
            booking.BookingId,
            cancellationToken);

        if (blockedUntil.HasValue && blockedUntil.Value > DateTime.Now)
        {
            return OperationResult.Failure(
                $"Xe vừa được trả thực tế. Cần đủ {booking.Policy.VehicleTurnaroundMinutes} phút để kiểm tra/vệ sinh; " +
                $"có thể xác nhận sẵn sàng từ {blockedUntil.Value:dd/MM/yyyy HH:mm}.");
        }

        return await _inner.MarkReadyForPickupAsync(bookingId, cancellationToken);
    }


    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "vài giây";
        }

        if (remaining.TotalHours >= 1)
        {
            var hours = (int)Math.Ceiling(remaining.TotalHours);
            return $"{hours} giờ";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} phút";
    }
}

