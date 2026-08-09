using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class BookingOperationService : IBookingOperationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public BookingOperationService(
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public Task<RefundResult> CancelByCustomerAsync(
        string customerId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        return CancelAsync(
            customerId,
            false,
            request,
            cancellationToken);
    }


    public Task<RefundResult> CancelByAdminAsync(
        string adminId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        return CancelAsync(
            adminId,
            true,
            request,
            cancellationToken);
    }

    // ============================================================
    // KHÁCH KHÔNG ĐẾN NHẬN XE
    //
    // Chính sách:
    // - Mất tiền thuê.
    // - Hoàn 100% tiền cọc vì xe chưa được bàn giao.
    // ============================================================

    public async Task<OperationResult> MarkNoShowAsync(
        int bookingId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(
                item => item.BookingId == bookingId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy đơn thuê.");
        }

        // Chỉ đơn đã thanh toán / sẵn sàng giao
        // nhưng chưa thực sự bàn giao xe mới được No-show.
        if (booking.Status is not
            (BookingStatus.Paid or BookingStatus.ReadyForPickup))
        {
            return OperationResult.Failure(
                "Chỉ đơn đã thanh toán nhưng chưa giao xe " +
                "mới được ghi nhận khách không đến nhận.");
        }

        // Phải quá giờ nhận ít nhất 30 phút.
        if (DateTime.Now <
            booking.PickupDate.AddMinutes(30))
        {
            return OperationResult.Failure(
                "Chỉ được ghi nhận khách không đến nhận xe " +
                "sau giờ nhận ít nhất 30 phút.");
        }


        var depositPaid =
            booking.Payments
                .Where(payment =>
                    payment.Status == PaymentStatus.Paid &&
                    payment.Type == PaymentType.Deposit)
                .Sum(payment => payment.Amount);

        booking.Status =
            BookingStatus.NoShow;

        booking.NoShowMarkedAt =
            DateTime.UtcNow;

        booking.CancelledBy =
            "Quản trị viên";

        booking.CancelReason =
            "Khách không đến nhận xe đúng thời gian quy định.";

        // No-show mất tiền thuê nhưng được hoàn cọc.
        booking.RefundAmount =
            depositPaid;

        booking.RefundReason =
            depositPaid > 0
                ? "Khách không đến nhận xe nên không được hoàn tiền thuê. " +
                  $"Hoàn toàn bộ tiền cọc {depositPaid:N0} đồng " +
                  "vì xe chưa được bàn giao."
                : "Khách không đến nhận xe nên không được hoàn tiền thuê. " +
                  "Đơn không có tiền cọc đã thanh toán để hoàn.";


        if (depositPaid > 0 &&
            !booking.Payments.Any(payment =>
                payment.Type == PaymentType.Refund))
        {
            booking.Payments.Add(
                new Payment
                {
                    Type =
                        PaymentType.Refund,

                    Amount =
                        depositPaid,

                    Method =
                        PaymentMethods.BankTransferRefund,

                    Status =
                        PaymentStatus.AwaitingRefund,

                    PaidAt =
                        null,

                    TransactionCode =
                        null
                });
        }

        booking.Vehicle.Status =
            await VehicleStatusResolver.ResolveAsync(
                _dbContext,
                booking.Vehicle,
                cancellationToken: cancellationToken);


        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    booking.CustomerId,

                Title =
                    "Đơn thuê ghi nhận khách không đến nhận xe",

                Message =
                    depositPaid > 0
                        ? $"Đơn #{booking.BookingId} đã được ghi nhận " +
                          "khách không đến nhận xe. " +
                          "Tiền thuê không được hoàn. " +
                          $"Tiền cọc {depositPaid:N0} đồng đang chờ hoàn."
                        : $"Đơn #{booking.BookingId} đã được ghi nhận " +
                          "khách không đến nhận xe. " +
                          "Tiền thuê không được hoàn."
            });

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);


        await _auditService.WriteAsync(
            adminId,
            "MarkNoShow",
            nameof(Booking),
            booking.BookingId.ToString(),
            depositPaid > 0
                ? $"Ghi nhận khách hàng {booking.CustomerId} không đến nhận xe. " +
                  $"Không hoàn tiền thuê. Hoàn tiền cọc: {depositPaid:N0} đồng."
                : $"Ghi nhận khách hàng {booking.CustomerId} không đến nhận xe. " +
                  "Không hoàn tiền thuê và không có tiền cọc để hoàn.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }


    private async Task<RefundResult> CancelAsync(
        string actorId,
        bool isAdmin,
        CancelBookingRequest request,
        CancellationToken cancellationToken)
    {


        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return RefundResult.Failure(
                "Vui lòng nhập lý do hủy đơn.");
        }

        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                cancellationToken);

        var query =
            _dbContext.Bookings
                .Include(item => item.Vehicle)
                .Include(item => item.Payments)
                .AsQueryable();

        // Nếu là khách hàng thì chỉ được thao tác đơn của mình.
        if (!isAdmin)
        {
            query =
                query.Where(item =>
                    item.CustomerId == actorId);
        }

        var booking =
            await query.FirstOrDefaultAsync(
                item =>
                    item.BookingId == request.BookingId,
                cancellationToken);

        if (booking is null)
        {
            return RefundResult.Failure(
                "Không tìm thấy đơn thuê.");
        }
        if (booking.Status is
            BookingStatus.Rented
            or BookingStatus.PendingInspection
            or BookingStatus.Completed
            or BookingStatus.Cancelled
            or BookingStatus.NoShow
            or BookingStatus.Rejected)
        {
            return RefundResult.Failure(
                "Trạng thái hiện tại không cho phép hủy đơn.");
        }

        var rentalLikePaid =
            booking.Payments
                .Where(payment =>
                    payment.Status == PaymentStatus.Paid &&
                    payment.Type is
                        PaymentType.Rental
                        or PaymentType.Extension)
                .Sum(payment =>
                    payment.Amount);

        var depositPaid =
            booking.Payments
                .Where(payment =>
                    payment.Status == PaymentStatus.Paid &&
                    payment.Type == PaymentType.Deposit)
                .Sum(payment =>
                    payment.Amount);
        decimal refundableRentalAmount;
        string rentalRefundReason;

  

        if (isAdmin)
        {
            refundableRentalAmount =
                rentalLikePaid;

            rentalRefundReason =
                rentalLikePaid > 0
                    ? "SmartCar chủ động hủy trước khi giao xe: " +
                      "hoàn 100% tiền thuê và gia hạn đã thanh toán."
                    : "Chưa phát sinh tiền thuê hoặc gia hạn đã thanh toán.";
        }

        else
        {
            var hoursBeforePickup =
                (booking.PickupDate - DateTime.Now)
                .TotalHours;

            // >= 48 giờ: hoàn 100%
            if (hoursBeforePickup >= 48)
            {
                refundableRentalAmount =
                    rentalLikePaid;

                rentalRefundReason =
                    rentalLikePaid > 0
                        ? "Khách hủy trước thời gian nhận xe " +
                          "từ 48 giờ trở lên: hoàn 100% " +
                          "tiền thuê và gia hạn."
                        : "Chưa phát sinh tiền thuê hoặc gia hạn đã thanh toán.";
            }

            // >= 24 và < 48 giờ: hoàn 50%
            else if (hoursBeforePickup >= 24)
            {
                refundableRentalAmount =
                    Math.Round(
                        rentalLikePaid * 0.5m,
                        0);

                rentalRefundReason =
                    rentalLikePaid > 0
                        ? "Khách hủy trước thời gian nhận xe " +
                          "từ 24 đến dưới 48 giờ: hoàn 50% " +
                          "tiền thuê và gia hạn."
                        : "Chưa phát sinh tiền thuê hoặc gia hạn đã thanh toán.";
            }

            // < 24 giờ: không hoàn tiền thuê
            else
            {
                refundableRentalAmount =
                    0;

                rentalRefundReason =
                    rentalLikePaid > 0
                        ? "Khách hủy trước thời gian nhận xe " +
                          "dưới 24 giờ: không hoàn tiền thuê và gia hạn."
                        : "Chưa phát sinh tiền thuê hoặc gia hạn đã thanh toán.";
            }
        }

        var refundAmount =
            refundableRentalAmount +
            depositPaid;


        string refundReason;

        if (depositPaid > 0)
        {
            refundReason =
                $"{rentalRefundReason} " +
                $"Hoàn 100% tiền cọc đã thanh toán: " +
                $"{depositPaid:N0} đồng.";
        }
        else
        {
            refundReason =
                rentalRefundReason;
        }

        // Không có bất kỳ khoản nào đã thanh toán.
        if (rentalLikePaid <= 0 &&
            depositPaid <= 0)
        {
            refundAmount =
                0;

            refundReason =
                "Đơn chưa phát sinh thanh toán nên " +
                "không có khoản hoàn tiền.";
        }

        booking.Status =
            BookingStatus.Cancelled;

        booking.CancelReason =
            request.Reason.Trim();

        booking.CancelledBy =
            isAdmin
                ? "Quản trị viên"
                : "Khách hàng";

        booking.CancelledAt =
            DateTime.UtcNow;

        booking.RefundAmount =
            refundAmount;

        booking.RefundReason =
            refundReason;

        booking.Vehicle.Status =
            await VehicleStatusResolver.ResolveAsync(
                _dbContext,
                booking.Vehicle,
                cancellationToken: cancellationToken);

        if (refundAmount > 0 &&
            !booking.Payments.Any(payment =>
                payment.Type == PaymentType.Refund))
        {
            booking.Payments.Add(
                new Payment
                {
                    Type =
                        PaymentType.Refund,

                    Amount =
                        refundAmount,

                    Method =
                        PaymentMethods.BankTransferRefund,

                    Status =
                        PaymentStatus.AwaitingRefund,

                    PaidAt =
                        null,

                    TransactionCode =
                        null
                });
        }

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    booking.CustomerId,

                Title =
                    "Đơn thuê đã được hủy",

                Message =
                    refundAmount > 0
                        ? $"Đơn #{booking.BookingId} đã hủy. " +
                          $"Khoản hoàn {refundAmount:N0} đồng " +
                          "đã được tạo và đang chờ xử lý."
                        : $"Đơn #{booking.BookingId} đã hủy " +
                          "và không phát sinh khoản hoàn tiền."
            });



        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);


        await _auditService.WriteAsync(
            actorId,
            isAdmin
                ? "AdminCancel"
                : "CustomerCancel",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"{(isAdmin ? "Quản trị viên" : "Khách hàng")} hủy đơn. " +
            $"Lý do: {booking.CancelReason}. " +
            $"Tiền thuê/gia hạn đã thanh toán: {rentalLikePaid:N0} đồng. " +
            $"Tiền thuê/gia hạn được hoàn: {refundableRentalAmount:N0} đồng. " +
            $"Tiền cọc được hoàn: {depositPaid:N0} đồng. " +
            $"Tổng hoàn tiền: {refundAmount:N0} đồng.",
            cancellationToken: cancellationToken);

        return RefundResult.Success(
            refundAmount);
    }
}