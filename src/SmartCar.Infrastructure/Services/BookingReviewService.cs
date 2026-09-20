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
                booking.ReturnDate,
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
                booking.PickupDate,
                booking.ReturnDate,
                booking.PickupMethod,
                booking.BookingId,
                cancellationToken))
        {
            var extra = booking.PickupMethod == VehiclePickupMethod.Delivery
                ? $" và thêm {RentalPolicy.DeliveryLeadMinutes} phút chuẩn bị giao tận nơi"
                : string.Empty;

            return OperationResult.Failure(
                $"Lịch xe không còn đủ {RentalPolicy.VehicleTurnaroundMinutes} phút xoay vòng{extra}.");
        }

        var blockedUntil = await _reservationPolicy.GetActualTurnaroundBlockedUntilAsync(
            booking.VehicleId,
            booking.PickupDate,
            booking.PickupMethod,
            booking.BookingId,
            cancellationToken);

        if (blockedUntil.HasValue && blockedUntil.Value > booking.PickupDate)
        {
            return OperationResult.Failure(
                $"Xe đang chờ kiểm tra sau lượt trả thực tế. Thời điểm nhận sớm nhất hiện tại là {blockedUntil.Value:dd/MM/yyyy HH:mm}.");
        }

        return OperationResult.Success();
    }
}
