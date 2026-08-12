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


    public HandoverService(
        ApplicationDbContext dbContext)
    {
        _dbContext =
            dbContext;
    }


    public async Task<OperationResult> CreateAsync(
        CreateHandoverRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
                    cancellationToken);


        // ============================================================
        // LẤY BOOKING + XE + PAYMENT + HANDOVER
        // ============================================================

        var booking =
            await _dbContext.Bookings

                .Include(item =>
                    item.Vehicle)

                .Include(item =>
                    item.Payments)

                .Include(item =>
                    item.Handover)

                .FirstOrDefaultAsync(
                    item =>
                        item.BookingId ==
                        request.BookingId,

                    cancellationToken);


        if (booking is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy đơn thuê.");
        }


        // ============================================================
        // KIỂM TRA TRẠNG THÁI ĐƠN
        // ============================================================

        if (booking.Status !=
            BookingStatus.ReadyForPickup)
        {
            return OperationResult.Failure(
                "Đơn chưa ở trạng thái sẵn sàng giao xe.");
        }


        // ============================================================
        // KHÔNG TẠO BIÊN BẢN LẦN HAI
        // ============================================================

        if (booking.Handover is not null)
        {
            return OperationResult.Failure(
                "Đơn đã có biên bản giao xe.");
        }


        // ============================================================
        // KIỂM TRA TIỀN THUÊ
        // ============================================================

        var rentalPaid =
            booking.Payments.Any(
                payment =>
                    payment.Type ==
                        PaymentType.Rental
                    &&
                    payment.Status ==
                        PaymentStatus.Paid);


        // ============================================================
        // KIỂM TRA TIỀN CỌC
        // ============================================================

        var depositPaidAmount =
            booking.Payments

                .Where(payment =>
                    payment.Type ==
                        PaymentType.Deposit
                    &&
                    payment.Status ==
                        PaymentStatus.Paid)

                .Sum(payment =>
                    payment.Amount);


        var depositSatisfied =
            booking.DepositAmount <= 0
            ||
            depositPaidAmount >=
                booking.DepositAmount;


        if (!rentalPaid ||
            !depositSatisfied)
        {
            return OperationResult.Failure(
                "Khách hàng phải thanh toán đủ tiền thuê và tiền cọc trước khi giao xe.");
        }


        // ============================================================
        // KIỂM TRA TRẠNG THÁI XE
        // ============================================================

        if (booking.Vehicle.Status !=
            VehicleStatus.Available)
        {
            return OperationResult.Failure(
                "Xe hiện không ở trạng thái sẵn sàng.");
        }


        // ============================================================
        // KIỂM TRA THỜI GIAN GIAO
        // ============================================================

        if (request.HandoverAt <
            booking.PickupDate)
        {
            return OperationResult.Failure(
                "Thời gian giao xe không được trước thời gian nhận xe đã đặt.");
        }


        if (request.HandoverAt >=
            booking.ReturnDate)
        {
            return OperationResult.Failure(
                "Thời gian giao xe phải trước thời gian trả xe đã đặt.");
        }


        // ============================================================
        // KIỂM TRA SỐ KM
        // ============================================================

        if (request.Mileage <
            booking.Vehicle.CurrentMileage)
        {
            return OperationResult.Failure(
                $"Số km giao xe không được nhỏ hơn số km hiện tại " +
                $"({booking.Vehicle.CurrentMileage:N0} km).");
        }


        // ============================================================
        // KIỂM TRA NHIÊN LIỆU
        // ============================================================

        if (string.IsNullOrWhiteSpace(
                request.FuelLevel))
        {
            return OperationResult.Failure(
                "Vui lòng ghi nhận mức nhiên liệu khi giao xe.");
        }


        // ============================================================
        // BẮT BUỘC XÁC NHẬN CHÍNH SÁCH
        //
        // Không chỉ tin validation ở View.
        // Service cũng phải kiểm tra.
        // ============================================================

        if (!request.PenaltyPolicyAccepted)
        {
            return OperationResult.Failure(
                "Cần xác nhận đã thông báo và khách đã đồng ý chính sách phí/phạt trước khi giao xe.");
        }


        // ============================================================
        // KIỂM TRA ẢNH BÀN GIAO
        // ============================================================

        if (string.IsNullOrWhiteSpace(
                request.ImagePaths))
        {
            return OperationResult.Failure(
                "Vui lòng tải ít nhất một ảnh tình trạng xe khi bàn giao.");
        }


        // ============================================================
        // TÍNH SỐ NGÀY THUÊ
        // ============================================================

        var rentalDays =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    (
                        booking.ReturnDate
                        -
                        booking.PickupDate
                    )
                    .TotalDays));


        // ============================================================
        // TÍNH KM ĐƯỢC SỬ DỤNG
        // ============================================================

        var includedKilometers =
            rentalDays
            *
            RentalPolicy.IncludedKilometersPerDay;


        // ============================================================
        // TẠO BIÊN BẢN
        //
        // QUAN TRỌNG:
        // Các giá trị phí KHÔNG lấy từ request.
        //
        // Luôn lấy trực tiếp từ RentalPolicy.
        // ============================================================

        booking.Handover =
            new VehicleHandover
            {
                HandoverAt =
                    request.HandoverAt,

                Mileage =
                    request.Mileage,

                FuelLevel =
                    request.FuelLevel.Trim(),

                ExteriorCondition =
                    Normalize(
                        request.ExteriorCondition),

                InteriorCondition =
                    Normalize(
                        request.InteriorCondition),

                Accessories =
                    Normalize(
                        request.Accessories),

                ImagePaths =
                    Normalize(
                        request.ImagePaths),

                Notes =
                    Normalize(
                        request.Notes),

                IncludedKilometers =
                    includedKilometers,

                ExcessKmFeePerKm =
                    RentalPolicy.ExcessKilometerFee,

                LateReturnFeeMultiplier =
                    RentalPolicy.LateReturnFeeMultiplier,

                TrafficFineTerms =
                    RentalPolicy.TrafficFineTerms,

                DamageCompensationTerms =
                    RentalPolicy.DamageCompensationTerms,

                PenaltyPolicyAccepted =
                    true
            };


        // ============================================================
        // BOOKING -> ĐANG THUÊ
        // ============================================================

        booking.Status =
            BookingStatus.Rented;


        // ============================================================
        // XE -> ĐANG ĐƯỢC THUÊ
        // ============================================================

        booking.Vehicle.Status =
            VehicleStatus.Rented;


        booking.Vehicle.CurrentMileage =
            request.Mileage;


        // ============================================================
        // THÔNG BÁO CHO KHÁCH
        // ============================================================

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    booking.CustomerId,

                Title =
                    "Đã bàn giao xe",

                Message =
                    $"Xe của đơn #{booking.BookingId} " +
                    $"đã được bàn giao thành công."
            });


        // ============================================================
        // LƯU DATABASE
        // ============================================================

        await _dbContext
            .SaveChangesAsync(
                cancellationToken);


        await transaction
            .CommitAsync(
                cancellationToken);


        return OperationResult.Success();
    }


    // ================================================================
    // CHUẨN HÓA TEXT
    // ================================================================

    private static string? Normalize(
        string? value)
    {
        return string.IsNullOrWhiteSpace(
            value)

            ? null

            : value.Trim();
    }
}