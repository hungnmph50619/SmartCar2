using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Domain.Constants;

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
        CancellationToken cancellationToken = default) =>
        CancelAsync(customerId, false, request, cancellationToken);

    public Task<RefundResult> CancelByAdminAsync(
        string adminId,
        CancelBookingRequest request,
        CancellationToken cancellationToken = default) =>
        CancelAsync(adminId, true, request, cancellationToken);

    public async Task<NoShowPreparationDto?> GetNoShowPreparationAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.BookingId == bookingId)
            .Select(booking => new NoShowPreparationDto(
                booking.BookingId,
                booking.PickupDate,
                booking.PickupMethod,
                booking.PickupLocation ?? string.Empty,
                _dbContext.Users
                    .Where(user => user.Id == booking.CustomerId)
                    .Select(user => user.PhoneNumber)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<OperationResult> MarkNoShowAsync(
        MarkNoShowRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status is not (BookingStatus.Paid or BookingStatus.ReadyForPickup))
        {
            return OperationResult.Failure("Chỉ đơn đã thanh toán nhưng chưa giao xe mới được ghi nhận khách không đến nhận.");
        }

        if (DateTime.Now < booking.PickupDate.AddMinutes(30))
        {
            return OperationResult.Failure(
                "Chỉ được ghi nhận khách không đến nhận xe sau giờ nhận ít nhất 30 phút.");
        }

        if (!request.ContactAttempted)
        {
            return OperationResult.Failure(
                "Admin phải xác nhận đã gọi hoặc nhắn cho khách trước khi ghi nhận NoShow (Khách không đến nhận)."
            );
        }

        if (string.IsNullOrWhiteSpace(request.ContactNote))
        {
            return OperationResult.Failure(
                "Vui lòng ghi lại thời gian và kết quả liên hệ với khách trước khi ghi nhận NoShow."
            );
        }

        var contactNote = request.ContactNote.Trim();
        if (contactNote.Length > 500)
        {
            return OperationResult.Failure("Ghi chú liên hệ tối đa 500 ký tự.");
        }

        var isHomeDelivery = booking.PickupMethod == DeliveryConstants.HomeDelivery;
        if (isHomeDelivery && !request.ArrivedAtPickupLocation)
        {
            return OperationResult.Failure(
                "Đơn giao tận nơi chỉ được ghi NoShow sau khi Admin xác nhận đã đến đúng địa điểm giao đã thỏa thuận."
            );
        }

        booking.Status = BookingStatus.NoShow;
        booking.NoShowMarkedAt = DateTime.UtcNow;
        booking.CancelledBy = "Quản trị viên";
        booking.CancelReason =
            $"Khách không đến nhận xe sau thời gian chờ 30 phút. " +
            $"Địa điểm giao: {booking.PickupLocation}. Liên hệ: {contactNote}";
        booking.RefundAmount = 0;
        booking.RefundReason = "Khách không đến nhận xe nên không được hoàn tiền.";
        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê ghi nhận khách không đến nhận xe",
            Message =
                $"Đơn #{booking.BookingId} đã được ghi nhận NoShow (Khách không đến nhận) " +
                "sau thời gian chờ và liên hệ theo quy trình. Đơn không phát sinh khoản hoàn tiền."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "MarkNoShow",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"Ghi nhận NoShow cho khách {booking.CustomerId}; giờ nhận {booking.PickupDate:dd/MM/yyyy HH:mm}; " +
            $"địa điểm {booking.PickupLocation}; giao tận nơi: {(isHomeDelivery ? "Có" : "Không")}; " +
            $"đã đến điểm giao: {(request.ArrivedAtPickupLocation ? "Có" : "Không áp dụng/Không")}; " +
            $"đã liên hệ: Có; ghi chú: {contactNote}; không hoàn tiền.",
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
            return RefundResult.Failure("Vui lòng nhập lý do hủy đơn.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var query = _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .AsQueryable();

        if (!isAdmin)
        {
            query = query.Where(item => item.CustomerId == actorId);
        }

        var booking = await query
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return RefundResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status is BookingStatus.Rented or BookingStatus.PendingInspection or
            BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.NoShow or BookingStatus.Rejected)
        {
            return RefundResult.Failure("Trạng thái hiện tại không cho phép hủy đơn.");
        }

        var hasRentalPaymentAwaitingConfirmation = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental &&
            payment.Status == PaymentStatus.AwaitingConfirmation);

        if (hasRentalPaymentAwaitingConfirmation)
        {
            return RefundResult.Failure(
                "Khoản chuyển khoản tiền thuê đang chờ SmartCar xác nhận. " +
                "Vui lòng xử lý giao dịch trước khi hủy đơn: nếu đã nhận tiền thì xác nhận thanh toán rồi thực hiện hủy để hệ thống tạo khoản hoàn; " +
                "nếu chưa nhận được tiền thì trả giao dịch về trạng thái chờ thanh toán rồi mới hủy đơn.");
        }

        var paidAmount = booking.Payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type is PaymentType.Rental or PaymentType.Extension)
            .Sum(payment => payment.Amount);

        decimal refundAmount;
        string refundReason;

        if (paidAmount <= 0)
        {
            refundAmount = 0;

            refundReason =
                "Đơn chưa phát sinh thanh toán nên không có khoản hoàn tiền.";
        }
        else if (isAdmin)
        {
            refundAmount = paidAmount;

            refundReason =
                "SmartCar chủ động hủy trước khi giao xe: hoàn 100% số tiền đã thanh toán.";
        }
        else
        {
            var hoursBeforePickup =
                (booking.PickupDate - DateTime.Now).TotalHours;

            if (hoursBeforePickup >= 48)
            {
                refundAmount = paidAmount;

                refundReason =
                    "Khách hủy trước thời gian nhận xe từ 48 giờ trở lên: hoàn 100%.";
            }
            else if (hoursBeforePickup >= 24)
            {
                refundAmount =
                    Math.Round(paidAmount * 0.5m, 0);

                refundReason =
                    "Khách hủy trước thời gian nhận xe từ 24 đến dưới 48 giờ: hoàn 50%.";
            }
            else
            {
                refundAmount = 0;

                refundReason =
                    "Khách hủy trước thời gian nhận xe dưới 24 giờ: không hoàn tiền.";
            }
        }

        foreach (var pendingRentalPayment in booking.Payments.Where(payment =>
                     payment.Type == PaymentType.Rental &&
                     payment.Status == PaymentStatus.Pending))
        {
            pendingRentalPayment.Status = PaymentStatus.Failed;
            pendingRentalPayment.PaidAt = null;
            // Không xóa TransactionCode: mã QRREQ gần nhất là bằng chứng đối soát
            // nếu trước đó Admin đã ghi nhận chưa tìm thấy giao dịch.
        }

        booking.Status = BookingStatus.Cancelled;
        booking.CancelReason = request.Reason.Trim();
        booking.CancelledBy = isAdmin ? "Quản trị viên" : "Khách hàng";
        booking.CancelledAt = DateTime.UtcNow;
        booking.RefundAmount = refundAmount;
        booking.RefundReason = refundReason;
        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

        if (refundAmount > 0 &&
            !booking.Payments.Any(payment =>
                payment.Type == PaymentType.Refund))
        {
            booking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = refundAmount,
                Method = PaymentMethods.BankTransferRefund,
                Status = PaymentStatus.AwaitingRefund,
                PaidAt = null,
                TransactionCode = null
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã được hủy",
            Message = refundAmount > 0
                ? $"Đơn #{booking.BookingId} đã hủy. " +
                  $"Khoản hoàn {refundAmount:N0} đồng đã được tạo và đang chờ xử lý."
                : $"Đơn #{booking.BookingId} đã hủy và không phát sinh khoản hoàn tiền."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            actorId,
            isAdmin ? "AdminCancel" : "CustomerCancel",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"{(isAdmin ? "Quản trị viên" : "Khách hàng")} hủy đơn. Lý do: {booking.CancelReason}. Hoàn tiền: {refundAmount:N0} đồng.",
            cancellationToken: cancellationToken);

        return RefundResult.Success(refundAmount);
    }
}
