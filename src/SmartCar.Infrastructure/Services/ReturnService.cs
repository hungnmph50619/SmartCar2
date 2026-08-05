using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Returns;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class ReturnService : IReturnService
{
    private readonly ApplicationDbContext _dbContext;

    public ReturnService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationResult> CreateAsync(
        CreateReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.Rented || booking.Handover is null)
        {
            return OperationResult.Failure("Chỉ đơn đang thuê và đã bàn giao xe mới được trả xe.");
        }

        if (booking.VehicleReturn is not null)
        {
            return OperationResult.Failure("Đơn đã có biên bản trả xe.");
        }

        if (request.Mileage < booking.Handover.Mileage)
        {
            return OperationResult.Failure("Số km trả xe không được nhỏ hơn số km lúc giao.");
        }

        if (request.ReturnedAt < booking.Handover.HandoverAt)
        {
            return OperationResult.Failure("Thời gian trả xe không được trước thời gian giao xe.");
        }

        if (string.IsNullOrWhiteSpace(request.FuelLevel))
        {
            return OperationResult.Failure("Vui lòng ghi nhận mức nhiên liệu khi trả xe.");
        }

        var lateMinutes = request.ReturnedAt > booking.ReturnDate
            ? (int)Math.Ceiling((request.ReturnedAt - booking.ReturnDate).TotalMinutes)
            : 0;
        var lateDays = lateMinutes > 0
            ? Math.Max(1, (int)Math.Ceiling(lateMinutes / 1440d))
            : 0;
        var lateFee = lateDays * booking.DailyPrice * 1.5m;

        var vehicleReturn = new VehicleReturn
        {
            ReturnedAt = request.ReturnedAt,
            Mileage = request.Mileage,
            FuelLevel = request.FuelLevel.Trim(),
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            HasDamage = request.HasDamage,
            IsLateReturn = lateMinutes > 0,
            LateMinutes = lateMinutes,
            LateFee = lateFee,
            ImagePaths = Normalize(request.ImagePaths),
            Notes = Normalize(request.Notes)
        };

        if (lateFee > 0)
        {
            vehicleReturn.AdditionalCharges.Add(new AdditionalCharge
            {
                ChargeType = AdditionalChargeType.LateReturn,
                Description = $"Phí trả xe muộn {lateMinutes} phút ({lateDays} ngày tính phí x 150%).",
                Amount = lateFee
            });
        }

        booking.VehicleReturn = vehicleReturn;
        booking.Status = BookingStatus.PendingInspection;
        booking.Vehicle.Status = VehicleStatus.Inspection;
        booking.Vehicle.CurrentMileage = request.Mileage;

        if (lateFee > 0)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Xe được ghi nhận trả muộn",
                Message = $"Đơn #{booking.BookingId} trả muộn {lateMinutes} phút. Phí trả muộn tạm tính: {lateFee:N0} đồng."
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AddChargeAsync(
        AddChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Description))
        {
            return OperationResult.Failure("Mô tả và số tiền phụ phí không hợp lệ.");
        }

        var booking = await ChargeQuery()
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null || booking.VehicleReturn is null)
        {
            return OperationResult.Failure("Không tìm thấy biên bản trả xe.");
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Chỉ đơn đang chờ kiểm tra mới được thêm phụ phí.");
        }

        if (HasPaidAdditionalCharge(booking))
        {
            return OperationResult.Failure("Không thể sửa phụ phí sau khi khách đã thanh toán.");
        }

        booking.VehicleReturn.AdditionalCharges.Add(new AdditionalCharge
        {
            ChargeType = request.ChargeType,
            Description = request.Description.Trim(),
            Amount = request.Amount
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveChargeAsync(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken = default)
    {
        var booking = await ChargeQuery()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null || booking.VehicleReturn is null)
        {
            return OperationResult.Failure("Không tìm thấy biên bản trả xe.");
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Chỉ đơn đang chờ kiểm tra mới được xóa phụ phí.");
        }

        if (HasPaidAdditionalCharge(booking))
        {
            return OperationResult.Failure("Không thể sửa phụ phí sau khi khách đã thanh toán.");
        }

        var charge = booking.VehicleReturn.AdditionalCharges
            .FirstOrDefault(item => item.AdditionalChargeId == additionalChargeId);

        if (charge is null)
        {
            return OperationResult.Failure("Không tìm thấy phụ phí.");
        }

        _dbContext.AdditionalCharges.Remove(charge);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateChargesAsync(booking, cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> CompleteAsync(
        int bookingId,
        bool requiresMaintenance,
        string? maintenanceNote,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.VehicleReturn)
            .Include(item => item.Payments)
            .Include(item => item.Extensions)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null || booking.VehicleReturn is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn hoặc biên bản trả xe.");
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Đơn chưa ở trạng thái chờ hoàn tất kiểm tra.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);
        var extensionPaid = booking.Extensions.All(extension =>
            extension.Status != BookingExtensionStatus.Approved);
        var additionalPaid = booking.AdditionalAmount <= 0 || booking.Payments.Any(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Status == PaymentStatus.Paid &&
            payment.Amount >= booking.AdditionalAmount);

        if (!rentalPaid || !extensionPaid || !additionalPaid)
        {
            return OperationResult.Failure("Đơn vẫn còn khoản tiền chưa được thanh toán.");
        }

        booking.Status = BookingStatus.Completed;
        booking.Vehicle.Status = requiresMaintenance
            ? VehicleStatus.Maintenance
            : VehicleStatus.Available;

        if (requiresMaintenance)
        {
            _dbContext.MaintenanceRecords.Add(new MaintenanceRecord
            {
                VehicleId = booking.VehicleId,
                StartDate = DateTime.UtcNow,
                Content = string.IsNullOrWhiteSpace(maintenanceNote)
                    ? "Kiểm tra hoặc sửa chữa sau lượt thuê"
                    : maintenanceNote.Trim(),
                Cost = 0,
                Mileage = booking.Vehicle.CurrentMileage,
                Status = MaintenanceStatus.InProgress
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã hoàn tất",
            Message = $"Đơn #{booking.BookingId} đã hoàn tất. Bạn có thể đánh giá trải nghiệm thuê xe."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private IQueryable<Booking> ChargeQuery() =>
        _dbContext.Bookings
            .Include(booking => booking.VehicleReturn)
                .ThenInclude(vehicleReturn => vehicleReturn!.AdditionalCharges)
            .Include(booking => booking.Payments);

    private async Task RecalculateChargesAsync(
        Booking booking,
        CancellationToken cancellationToken)
    {
        booking.AdditionalAmount = await _dbContext.AdditionalCharges
            .Where(charge => charge.VehicleReturn.BookingId == booking.BookingId)
            .SumAsync(charge => (decimal?)charge.Amount, cancellationToken) ?? 0;
        booking.TotalAmount = booking.RentalAmount + booking.AdditionalAmount;

        var pendingPayment = booking.Payments.FirstOrDefault(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Status == PaymentStatus.Pending);

        if (booking.AdditionalAmount > 0)
        {
            if (pendingPayment is null)
            {
                booking.Payments.Add(new Payment
                {
                    Type = PaymentType.AdditionalCharge,
                    Amount = booking.AdditionalAmount,
                    Method = "Mo phong",
                    Status = PaymentStatus.Pending
                });
            }
            else
            {
                pendingPayment.Amount = booking.AdditionalAmount;
            }
        }
        else if (pendingPayment is not null)
        {
            _dbContext.Payments.Remove(pendingPayment);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool HasPaidAdditionalCharge(Booking booking) =>
        booking.Payments.Any(payment =>
            payment.Type == PaymentType.AdditionalCharge &&
            payment.Status == PaymentStatus.Paid);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
