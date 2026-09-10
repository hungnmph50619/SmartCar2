using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class PolicyAwareExtensionService : IExtensionService
{
    private const string ForceMajeureMarker = "[FORCE_MAJEURE]";

    private readonly ExtensionService _inner;
    private readonly BookingReservationPolicy _reservationPolicy;
    private readonly ApplicationDbContext _dbContext;

    public PolicyAwareExtensionService(
        ExtensionService inner,
        BookingReservationPolicy reservationPolicy,
        ApplicationDbContext dbContext)
    {
        _inner = inner;
        _reservationPolicy = reservationPolicy;
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ExtensionDto>> GetCustomerExtensionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        await _reservationPolicy.ExpireStaleReservationsAsync(cancellationToken);
        return await _inner.GetCustomerExtensionsAsync(customerId, cancellationToken);
    }

    public async Task<IReadOnlyList<ExtensionDto>> GetPendingExtensionsAsync(
        CancellationToken cancellationToken = default)
    {
        await _reservationPolicy.ExpireStaleReservationsAsync(cancellationToken);
        return await _inner.GetPendingExtensionsAsync(cancellationToken);
    }

    public async Task<OperationResult> RequestAsync(
        string customerId,
        RequestExtensionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _reservationPolicy.ExpireStaleReservationsAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.BookingId == request.BookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return await _inner.RequestAsync(customerId, request, cancellationToken);
        }

        var bufferedConflict = await _reservationPolicy.HasBufferedConflictAsync(
            booking.VehicleId,
            booking.PickupDate,
            request.RequestedReturnDate,
            booking.BookingId,
            cancellationToken);

        if (bufferedConflict && !request.IsForceMajeure)
        {
            return OperationResult.Failure(
                $"Không thể gia hạn đến {request.RequestedReturnDate:dd/MM/yyyy HH:mm}: " +
                $"phải chừa ít nhất {RentalPolicy.VehicleTurnaroundMinutes} phút để nhận, kiểm tra và chuẩn bị xe trước lượt kế tiếp. " +
                "Nếu thực sự bất khả kháng, hãy gửi yêu cầu bất khả kháng kèm minh chứng để Admin xử lý xung đột riêng.");
        }

        return await _inner.RequestAsync(customerId, request, cancellationToken);
    }

    public async Task<OperationResult> ApproveAsync(
        int extensionId,
        string adminId,
        bool confirmConflictHandled = false,
        string? adminNote = null,
        CancellationToken cancellationToken = default)
    {
        await _reservationPolicy.ExpireStaleReservationsAsync(cancellationToken);

        var extension = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        if (extension is not null)
        {
            var bufferedConflict = await _reservationPolicy.HasBufferedConflictAsync(
                extension.Booking.VehicleId,
                extension.Booking.PickupDate,
                extension.RequestedReturnDate,
                extension.BookingId,
                cancellationToken);

            if (bufferedConflict)
            {
                var forceMajeure = !string.IsNullOrWhiteSpace(extension.CustomerNote) &&
                    extension.CustomerNote.Contains(ForceMajeureMarker, StringComparison.Ordinal);

                if (!forceMajeure)
                {
                    return OperationResult.Failure(
                        $"Không thể duyệt gia hạn: sau giờ trả mới phải còn ít nhất " +
                        $"{RentalPolicy.VehicleTurnaroundMinutes} phút trước lượt thuê kế tiếp.");
                }

                if (!confirmConflictHandled)
                {
                    return OperationResult.Failure(
                        "Gia hạn bất khả kháng làm mất khoảng xoay vòng của đơn kế tiếp. " +
                        "Chỉ duyệt sau khi đã xử lý phương án đổi xe/hoàn tiền cho khách kế tiếp và xác nhận đã xử lý xung đột.");
                }
            }
        }

        return await _inner.ApproveAsync(
            extensionId,
            adminId,
            confirmConflictHandled,
            adminNote,
            cancellationToken);
    }

    public Task<OperationResult> RequestMoreEvidenceAsync(
        int extensionId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default) =>
        _inner.RequestMoreEvidenceAsync(extensionId, adminId, reason, cancellationToken);

    public Task<OperationResult> SupplementEvidenceAsync(
        int extensionId,
        string customerId,
        string evidenceNote,
        string? customerNote = null,
        CancellationToken cancellationToken = default) =>
        _inner.SupplementEvidenceAsync(
            extensionId,
            customerId,
            evidenceNote,
            customerNote,
            cancellationToken);

    public Task<OperationResult> RejectAsync(
        int extensionId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default) =>
        _inner.RejectAsync(extensionId, adminId, reason, cancellationToken);

    public Task<OperationResult> MarkPaidAsync(
        int bookingId,
        CancellationToken cancellationToken = default) =>
        _inner.MarkPaidAsync(bookingId, cancellationToken);
}
