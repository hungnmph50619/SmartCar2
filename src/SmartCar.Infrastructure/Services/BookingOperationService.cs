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
    private const decimal NoShowRentalPenaltyRate = 0.40m;
    private const int NoShowGraceMinutes = 30;
    private const int MinimumNoShowContactAttempts = 2;
    private const string PickupDepartureAuditAction = "ConfirmPickupDeparture";

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

    public async Task<PickupPreparationDto?> GetPickupPreparationAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.BookingId == bookingId)
            .Select(booking => new PickupPreparationDto(
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

    public async Task<OperationResult> ConfirmPickupDepartureAsync(
        ConfirmPickupDepartureRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        if (!request.CustomerConfirmed)
        {
            return OperationResult.Failure(
                "Chỉ được chuẩn bị xuất phát sau khi khách đã nghe máy/nhắn lại và xác nhận sẽ nhận xe.");
        }

        if (string.IsNullOrWhiteSpace(request.ContactNote))
        {
            return OperationResult.Failure(
                "Vui lòng ghi thời gian và nội dung khách đã xác nhận trước khi Admin đi giao xe.");
        }

        var contactNote = request.ContactNote.Trim();
        if (contactNote.Length > 500)
        {
            return OperationResult.Failure("Ghi chú xác nhận trước khi giao tối đa 500 ký tự.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental && payment.Status == PaymentStatus.Paid);

        if (booking.Status != BookingStatus.Paid || !rentalPaid)
        {
            return OperationResult.Failure(
                "Chỉ đơn đã thanh toán và chưa chuyển sang sẵn sàng giao xe mới được xác nhận trước khi xuất phát.");
        }

        if (DateTime.Now >= booking.PickupDate)
        {
            return OperationResult.Failure(
                "Đã đến hoặc quá giờ nhận xe. Bước xác nhận này phải được thực hiện trước giờ hẹn để tránh Admin đi giao khi khách chưa xác nhận.");
        }

        if (string.IsNullOrWhiteSpace(booking.PickupLocation) ||
            string.IsNullOrWhiteSpace(booking.ReturnLocation))
        {
            return OperationResult.Failure(
                "Vui lòng chốt địa điểm nhận và trả xe trước khi chuẩn bị đi giao.");
        }

        booking.Status = BookingStatus.ReadyForPickup;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "SmartCar đã xác nhận lịch nhận xe",
            Message =
                $"SmartCar đã ghi nhận bạn xác nhận nhận xe đơn #{booking.BookingId} tại " +
                $"{booking.PickupLocation} lúc {booking.PickupDate:dd/MM/yyyy HH:mm}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            PickupDepartureAuditAction,
            nameof(Booking),
            booking.BookingId.ToString(),
            $"Trước khi xuất phát giao xe, Admin đã liên hệ và khách xác nhận sẽ nhận xe; " +
            $"giờ hẹn {booking.PickupDate:dd/MM/yyyy HH:mm}; địa điểm {booking.PickupLocation}; ghi chú: {contactNote}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<NoShowPreparationDto?> GetNoShowPreparationAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new
            {
                item.BookingId,
                item.PickupDate,
                item.PickupMethod,
                PickupLocation = item.PickupLocation ?? string.Empty,
                CustomerPhone = _dbContext.Users
                    .Where(user => user.Id == item.CustomerId)
                    .Select(user => user.PhoneNumber)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return null;
        }

        var bookingIdText = bookingId.ToString();
        var preDepartureAudit = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == PickupDepartureAuditAction &&
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingIdText)
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => new { log.CreatedAt, log.Description })
            .FirstOrDefaultAsync(cancellationToken);

        return new NoShowPreparationDto(
            booking.BookingId,
            booking.PickupDate,
            booking.PickupMethod,
            booking.PickupLocation,
            booking.CustomerPhone,
            preDepartureAudit?.CreatedAt,
            preDepartureAudit?.Description);
    }

    public async Task<OperationResult> MarkNoShowAsync(
        MarkNoShowRequest request,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == request.BookingId, cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê.");
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            return OperationResult.Failure(
                "Chỉ đơn đã được khách xác nhận trước khi giao và đang ở trạng thái sẵn sàng bàn giao mới được ghi nhận NoShow.");
        }

        if (DateTime.Now < booking.PickupDate.AddMinutes(NoShowGraceMinutes))
        {
            return OperationResult.Failure(
                $"Chỉ được ghi nhận khách không đến nhận xe sau giờ nhận ít nhất {NoShowGraceMinutes} phút.");
        }

        var bookingIdText = booking.BookingId.ToString();
        var hasPreDepartureConfirmation = await _dbContext.AuditLogs.AnyAsync(log =>
            log.Action == PickupDepartureAuditAction &&
            log.EntityName == nameof(Booking) &&
            log.EntityId == bookingIdText,
            cancellationToken);

        if (!hasPreDepartureConfirmation)
        {
            return OperationResult.Failure(
                "Không tìm thấy bằng chứng Admin đã liên hệ và khách xác nhận nhận xe trước khi xuất phát. Không được kết luận NoShow theo quy trình giao xe tận nơi.");
        }

        if (request.ContactAttemptCount < MinimumNoShowContactAttempts)
        {
            return OperationResult.Failure(
                $"Admin phải liên hệ lại khách ít nhất {MinimumNoShowContactAttempts} lần trong thời gian chờ trước khi ghi nhận NoShow.");
        }

        if (string.IsNullOrWhiteSpace(request.ContactNote))
        {
            return OperationResult.Failure(
                "Vui lòng ghi lại thời gian và kết quả từng lần liên hệ với khách trước khi ghi nhận NoShow.");
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
                "Đơn giao tận nơi chỉ được ghi NoShow sau khi Admin xác nhận đã đến đúng địa điểm giao đã thỏa thuận.");
        }

        var paidRentalAmount = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Rental &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var rentalPenalty = Math.Round(
            booking.RentalAmount * NoShowRentalPenaltyRate,
            0,
            MidpointRounding.AwayFromZero);
        var incurredPickupDeliveryFee = isHomeDelivery
            ? Math.Round(
                booking.PickupDeliveryDistanceKm * booking.DeliveryRatePerKm,
                0,
                MidpointRounding.AwayFromZero)
            : 0;
        var retainedAmount = Math.Min(
            paidRentalAmount,
            Math.Max(0, rentalPenalty + incurredPickupDeliveryFee));
        var refundAmount = Math.Max(0, paidRentalAmount - retainedAmount);

        booking.Status = BookingStatus.NoShow;
        booking.NoShowMarkedAt = DateTime.UtcNow;
        booking.CancelledBy = "Quản trị viên";
        booking.CancelReason =
            $"Khách không đến nhận xe sau thời gian chờ {NoShowGraceMinutes} phút. " +
            $"Địa điểm giao: {booking.PickupLocation}. Admin đã liên hệ lại {request.ContactAttemptCount} lần. " +
            $"Ghi nhận liên hệ: {contactNote}";
        booking.RefundAmount = refundAmount;
        booking.RefundReason =
            $"Chính sách NoShow: giữ {NoShowRentalPenaltyRate:P0} tiền thuê ({rentalPenalty:N0} đồng)" +
            (incurredPickupDeliveryFee > 0
                ? $" và phí lượt giao xe thực tế ({incurredPickupDeliveryFee:N0} đồng)"
                : string.Empty) +
            $"; tổng giữ lại {retainedAmount:N0} đồng; hoàn phần còn lại {refundAmount:N0} đồng.";

        booking.Vehicle.Status = await VehicleStatusResolver.ResolveAsync(
            _dbContext,
            booking.Vehicle,
            cancellationToken: cancellationToken);

        if (refundAmount > 0 &&
            !booking.Payments.Any(payment => payment.Type == PaymentType.Refund))
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
            Title = "Đơn thuê ghi nhận khách không đến nhận xe",
            Message = refundAmount > 0
                ? $"Đơn #{booking.BookingId} đã được ghi nhận NoShow sau thời gian chờ và liên hệ theo quy trình. " +
                  $"SmartCar giữ lại {retainedAmount:N0} đồng theo chính sách NoShow; khoản hoàn {refundAmount:N0} đồng đã được tạo và đang chờ xử lý."
                : $"Đơn #{booking.BookingId} đã được ghi nhận NoShow sau thời gian chờ và liên hệ theo quy trình. " +
                  $"Khoản giữ lại theo chính sách là {retainedAmount:N0} đồng nên không còn số tiền phải hoàn."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "MarkNoShow",
            nameof(Booking),
            booking.BookingId.ToString(),
            $"Ghi nhận NoShow cho khách {booking.CustomerId}; giờ nhận {booking.PickupDate:dd/MM/yyyy HH:mm}; " +
            $"địa điểm {booking.PickupLocation}; giao tận nơi: {(isHomeDelivery ? "Có" : "Không")}; " +
            $"đã đến điểm giao: {(request.ArrivedAtPickupLocation ? "Có" : "Không áp dụng")}; " +
            $"số lần liên hệ lại: {request.ContactAttemptCount}; ghi chú: {contactNote}; " +
            $"giữ lại {retainedAmount:N0} đồng; hoàn {refundAmount:N0} đồng.",
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
