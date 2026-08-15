using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class HandoverService : IHandoverService
{
    private static readonly HashSet<string> FuelGaugeLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "8/8 (100%)",
        "7/8 (~87.5%)",
        "6/8 (75%)",
        "5/8 (~62.5%)",
        "4/8 (50%)",
        "3/8 (~37.5%)",
        "2/8 (25%)",
        "1/8 (~12.5%)",
        "0/8 (Gần cạn)"
    };

    private readonly ApplicationDbContext _dbContext;

    public HandoverService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<HandoverVehicleContextDto?> GetVehicleContextAsync(
        int bookingId,
        CancellationToken cancellationToken = default) =>
        _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.BookingId == bookingId)
            .Select(booking => new HandoverVehicleContextDto(
                booking.Vehicle.CurrentMileage,
                booking.Vehicle.FuelType))
            .FirstOrDefaultAsync(cancellationToken);

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
            return OperationResult.Failure("Đơn đã có hồ sơ bàn giao xe.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);
        var depositPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid);

        if (!rentalPaid || !depositPaid)
        {
            return OperationResult.Failure(
                "Chỉ được bàn giao xe sau khi SmartCar đã xác nhận đủ tiền thuê và cọc bảo đảm.");
        }

        if (booking.Vehicle.Status != VehicleStatus.Available)
        {
            return OperationResult.Failure("Xe hiện không ở trạng thái sẵn sàng.");
        }

        if (!RentalPolicyConstants.DemoBypassRentalTimelineValidation)
        {
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
        }

        if (request.Mileage < booking.Vehicle.CurrentMileage)
        {
            return OperationResult.Failure(
                $"Số km khi giao không được nhỏ hơn số km hiện tại của xe ({booking.Vehicle.CurrentMileage:N0} km).");
        }

        var fuelLevelResult = NormalizeFuelLevel(
            booking.Vehicle.FuelType,
            request.FuelLevel);
        if (!fuelLevelResult.Succeeded)
        {
            return OperationResult.Failure(fuelLevelResult.Error!);
        }

        if (string.IsNullOrWhiteSpace(request.ImagePaths))
        {
            return OperationResult.Failure("Thiếu ảnh hiện trạng xe hoặc ảnh biên bản bàn giao đã ký.");
        }

        booking.Handover = new VehicleHandover
        {
            HandoverAt = request.HandoverAt,
            Mileage = request.Mileage,
            FuelLevel = fuelLevelResult.Value!,
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            Accessories = null,
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
            Message =
                $"Đơn #{booking.BookingId} đã hoàn tất bàn giao. SmartCar đã lưu ảnh hiện trạng xe và ảnh biên bản giấy có chữ ký của bạn trong hồ sơ chuyến thuê. " +
                "Cọc bảo đảm sẽ được quyết toán sau khi xe được trả và kiểm tra."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static (bool Succeeded, string? Value, string? Error) NormalizeFuelLevel(
        string vehicleFuelType,
        string? rawFuelLevel)
    {
        if (string.IsNullOrWhiteSpace(rawFuelLevel))
        {
            return (false, null, "Vui lòng ghi nhận mức nhiên liệu khi giao xe.");
        }

        var value = rawFuelLevel.Trim();
        if (string.Equals(vehicleFuelType, "Điện", StringComparison.OrdinalIgnoreCase))
        {
            var numericValue = value.TrimEnd('%').Trim();
            if (!int.TryParse(numericValue, out var batteryPercent) ||
                batteryPercent < 0 || batteryPercent > 100)
            {
                return (false, null, "Mức pin xe điện phải từ 0% đến 100%.");
            }

            return (true, $"{batteryPercent}%", null);
        }

        if (!FuelGaugeLevels.Contains(value))
        {
            return (false, null,
                "Mức nhiên liệu xe xăng/dầu/hybrid phải được ghi theo vạch 0/8 đến 8/8 trên đồng hồ.");
        }

        return (true, value, null);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
