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
    private readonly ApplicationDbContext _dbContext;

    public HandoverService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken);

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
            payment.Type == PaymentType.Rental &&
            payment.Status == PaymentStatus.Paid);

        var depositPaidAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var depositRefundPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);

        var depositSatisfied = booking.DepositAmount <= 0 ||
            Math.Max(0m, depositPaidAmount - depositRefundPlanned) >= booking.DepositAmount;

        var hasOpenSwapPayment = booking.Payments.Any(payment =>
            payment.Type == PaymentType.VehicleSwapAdjustment &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation);
        var paidRentalDays = Math.Max(
    1,
    (int)Math.Ceiling(
        (booking.ReturnDate - booking.PickupDate)
        .TotalHours / 24d));

        var effectiveIncludedKilometers = Math.Max(
            booking.Handover.IncludedKilometers,
            paidRentalDays * RentalPolicy.IncludedKilometersPerDay);
        if (hasOpenSwapPayment)
        {
            return OperationResult.Failure(
                "Khách cần thanh toán xong chênh lệch đổi xe trước khi giao xe.");
        }

        if (!rentalPaid || !depositSatisfied)
        {
            return OperationResult.Failure(
                "Khách phải thanh toán đủ tiền thuê và tiền cọc trước khi giao xe.");
        }

        if (booking.Vehicle.Status != VehicleStatus.Available)
        {
            return OperationResult.Failure("Xe hiện không ở trạng thái sẵn sàng.");
        }
        if (request.HandoverAt > DateTime.Now.AddMinutes(5))
        {
            return OperationResult.Failure(
                "Thời gian giao xe không được ở tương lai.");
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
            return OperationResult.Failure(
                $"Số km giao xe không được nhỏ hơn số km hiện tại ({booking.Vehicle.CurrentMileage:N0} km).");
        }

        if (!TryParseFuelPercent(request.FuelLevel, out var fuelPercent))
        {
            return OperationResult.Failure(
                "Mức nhiên liệu khi giao phải là số từ 0 đến 100%.");
        }

        if (!request.PenaltyPolicyAccepted)
        {
            return OperationResult.Failure(
                "Cần xác nhận đã thông báo và khách đã đồng ý chính sách phí/phạt trước khi giao xe.");
        }

        if (string.IsNullOrWhiteSpace(request.ImagePaths))
        {
            return OperationResult.Failure(
                "Biên bản giao xe phải có ảnh đối chiếu tình trạng xe.");
        }

        var rentalDays = Math.Max(
            1,
            (int)Math.Ceiling((booking.ReturnDate - booking.PickupDate).TotalDays));

        booking.Handover = new VehicleHandover
        {
            HandoverAt = request.HandoverAt,
            Mileage = request.Mileage,
            FuelLevel = $"{fuelPercent}%",
            ExteriorCondition = Normalize(request.ExteriorCondition),
            InteriorCondition = Normalize(request.InteriorCondition),
            Accessories = Normalize(request.Accessories),
            ImagePaths = Normalize(request.ImagePaths),
            Notes = Normalize(request.Notes),
            IncludedKilometers = rentalDays * RentalPolicy.IncludedKilometersPerDay,
            ExcessKmFeePerKm = RentalPolicy.ExcessKilometerFee,
            LateReturnFeeMultiplier = RentalPolicy.LateReturnFeeMultiplier,
            TrafficFineTerms = RentalPolicy.TrafficFineTerms,
            DamageCompensationTerms = RentalPolicy.DamageCompensationTerms,
            PenaltyPolicyAccepted = true
        };

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    private static bool TryParseFuelPercent(string? value, out int percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith('%'))
        {
            normalized = normalized[..^1].Trim();
        }

        return int.TryParse(normalized, out percent) && percent is >= 0 and <= 100;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}