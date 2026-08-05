using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class HandoverService : IHandoverService
{
    private readonly ApplicationDbContext _dbContext;

    public HandoverService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            return OperationResult.Failure("Đơn chưa ở trạng thái sẵn sàng giao xe.");
        }

        if (booking.Handover is not null)
        {
            return OperationResult.Failure("Đơn đã có biên bản giao xe.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);

        if (!rentalPaid)
        {
            return OperationResult.Failure("Khách hàng chưa thanh toán tiền thuê.");
        }

        if (booking.Vehicle.Status != VehicleStatus.Available)
        {
            return OperationResult.Failure("Xe hiện không ở trạng thái sẵn sàng.");
        }

        if (request.HandoverAt < booking.PickupDate)
        {
            return OperationResult.Failure(
                "Thời gian giao xe không được trước thời gian nhận xe đã đặt.");
        }

        if (request.HandoverAt >= booking.ReturnDate)
        {
            return OperationResult.Failure(
                "Thời gian giao xe phải trước thời gian trả xe đã đặt.");
        }

        if (request.Mileage < booking.Vehicle.CurrentMileage)
        {
            return OperationResult.Failure("Số km giao xe không được nhỏ hơn số km hiện tại.");
        }

        if (string.IsNullOrWhiteSpace(request.FuelLevel))
        {
            return OperationResult.Failure("Vui lòng ghi nhận mức nhiên liệu khi giao xe.");
        }

        booking.Handover = new VehicleHandover
        {
            HandoverAt = request.HandoverAt,
            Mileage = request.Mileage,
            FuelLevel = request.FuelLevel.Trim(),
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            Accessories = Normalize(request.Accessories),
            ImagePaths = Normalize(request.ImagePaths),
            Notes = Normalize(request.Notes)
        };

        booking.Status = BookingStatus.Rented;
        booking.Vehicle.Status = VehicleStatus.Rented;
        booking.Vehicle.CurrentMileage = request.Mileage;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã bàn giao xe",
            Message = $"Xe của đơn #{booking.BookingId} đã được bàn giao thành công."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
