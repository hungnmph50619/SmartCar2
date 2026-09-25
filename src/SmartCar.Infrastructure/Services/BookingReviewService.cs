using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class BookingReviewService : IBookingReviewService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IDocumentService _documentService;
    private readonly BookingReservationPolicy _reservationPolicy;

    public BookingReviewService(
        ApplicationDbContext dbContext,
        IDocumentService documentService,
        BookingReservationPolicy reservationPolicy)
    {
        _dbContext = dbContext;
        _documentService = documentService;
        _reservationPolicy = reservationPolicy;
    }

    public async Task<OperationResult> ValidateForStaffReviewAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        await _reservationPolicy.ExpireStaleReservationsAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.PendingConfirmation)
        {
            return OperationResult.Failure("Chỉ kiểm tra đơn đang ở trạng thái Chờ xác nhận.");
        }

        var (reviewPickup, reviewReturn) = CounterRentalSchedule.ForApproval(
            booking.Source, booking.IsImmediateCounterRental, booking.CreatedAt,
            booking.PickupDate, booking.ReturnDate, DateTime.Now);

        if (booking.ReservationExpiresAt.HasValue && booking.ReservationExpiresAt <= DateTime.UtcNow)
        {
            return OperationResult.Failure("Đơn đã hết thời gian giữ chỗ.");
        }

        var customerActive = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == booking.CustomerId && user.IsActive, cancellationToken);

        if (!customerActive)
        {
            return OperationResult.Failure("Tài khoản khách hàng không còn hoạt động.");
        }

        if (!await _documentService.HasValidRentalDocumentsAsync(
                booking.CustomerId,
                reviewReturn,
                cancellationToken))
        {
            return OperationResult.Failure(
                "CCCD/GPLX của khách chưa được quản trị viên xác minh đầy đủ hoặc không còn hiệu lực đến ngày trả xe.");
        }

        if (booking.Vehicle.Status is VehicleStatus.Maintenance or VehicleStatus.Inactive)
        {
            return OperationResult.Failure("Xe đang bảo trì hoặc ngừng hoạt động nên không thể gửi duyệt đơn này.");
        }

        if (await _reservationPolicy.HasBufferedConflictAsync(
                booking.VehicleId,
                reviewPickup,
                reviewReturn,
                booking.PickupMethod,
                booking.BookingId,
                cancellationToken))
        {
            return OperationResult.Failure(
                $"Lịch xe không còn đủ {booking.Policy.VehicleTurnaroundMinutes} phút chuẩn bị giữa hai lượt thuê.");
        }

        var blockedUntil = await _reservationPolicy.GetActualTurnaroundBlockedUntilAsync(
            booking.VehicleId,
            reviewPickup,
            booking.PickupMethod,
            booking.BookingId,
            cancellationToken);

        if (blockedUntil.HasValue && blockedUntil.Value > reviewPickup)
        {
            return OperationResult.Failure(
                $"Xe đang chờ kiểm tra sau lượt trả thực tế. Thời điểm nhận sớm nhất hiện tại là {blockedUntil.Value:dd/MM/yyyy HH:mm}.");
        }

        return OperationResult.Success();
    }
}
