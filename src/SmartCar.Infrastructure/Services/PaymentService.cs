using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class PaymentService : IPaymentService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public PaymentService(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<AdminPaymentListItemDto>> GetAdminPaymentsAsync(
        PaymentStatus? status = null,
        PaymentType? type = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Payments.AsNoTracking().AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(payment => payment.Status == status.Value);
        }

        if (type.HasValue)
        {
            query = query.Where(payment => payment.Type == type.Value);
        }

        return await query
            .OrderByDescending(payment => payment.PaymentId)
            .Select(payment => new AdminPaymentListItemDto(
                payment.PaymentId,
                payment.BookingId,
                payment.Booking.CustomerId,
                _dbContext.Users
                    .Where(user => user.Id == payment.Booking.CustomerId)
                    .Select(user => user.FullName)
                    .FirstOrDefault() ?? string.Empty,
                payment.Booking.Vehicle.VehicleName,
                payment.Booking.Vehicle.LicensePlate,
                payment.Type,
                payment.Amount,
                payment.Method,
                payment.Status,
                payment.PaidAt,
                payment.TransactionCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> SimulatePaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .Include(item => item.Extensions)
            .FirstOrDefaultAsync(
                item => item.BookingId == bookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (paymentType == PaymentType.Refund)
        {
            return OperationResult.Failure("Khoản hoàn tiền chỉ do hệ thống và quản trị viên xử lý.");
        }

        if (paymentType == PaymentType.Rental && booking.Status != BookingStatus.PendingPayment)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán tiền thuê.");
        }

        if (paymentType == PaymentType.Extension &&
            (booking.Status != BookingStatus.Rented ||
             !booking.Extensions.Any(extension => extension.Status == BookingExtensionStatus.Approved)))
        {
            return OperationResult.Failure("Không có yêu cầu gia hạn đã duyệt đang chờ thanh toán.");
        }

        if (paymentType == PaymentType.AdditionalCharge && booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán phụ phí.");
        }

        if (booking.Payments.Any(item =>
                item.Type == paymentType &&
                item.Status == PaymentStatus.AwaitingConfirmation))
        {
            return OperationResult.Failure(
                "Khoản thanh toán đang chờ quản trị viên xác nhận chuyển khoản QR. Không thể thanh toán lại bằng mô phỏng.");
        }

        var payment = booking.Payments.FirstOrDefault(item =>
            item.Type == paymentType && item.Status == PaymentStatus.Pending);

        if (payment is null &&
            paymentType == PaymentType.AdditionalCharge &&
            booking.AdditionalAmount > 0)
        {
            payment = new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.AdditionalCharge,
                Amount = booking.AdditionalAmount,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending
            };

            booking.Payments.Add(payment);
        }

        if (payment is null)
        {
            var alreadyPaid = booking.Payments.Any(item =>
                item.Type == paymentType && item.Status == PaymentStatus.Paid);

            return alreadyPaid
                ? OperationResult.Failure("Khoản tiền này đã được thanh toán.")
                : OperationResult.Failure("Không tìm thấy khoản thanh toán phù hợp.");
        }

        payment.Method = PaymentMethods.Simulation;
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = $"SC{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        if (paymentType == PaymentType.Rental)
        {
            booking.Status = BookingStatus.Paid;
        }
        else if (paymentType == PaymentType.Extension)
        {
            var extension = booking.Extensions
                .Where(item => item.Status == BookingExtensionStatus.Approved)
                .OrderByDescending(item => item.RequestedAt)
                .FirstOrDefault();

            if (extension is null)
            {
                return OperationResult.Failure("Không tìm thấy yêu cầu gia hạn đang chờ thanh toán.");
            }

            extension.Status = BookingExtensionStatus.Paid;
            extension.PaidAt = DateTime.UtcNow;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Thanh toán thành công",
            Message = paymentType switch
            {
                PaymentType.Rental => $"Đơn #{booking.BookingId} đã thanh toán tiền thuê thành công.",
                PaymentType.Extension => $"Đơn #{booking.BookingId} đã thanh toán tiền gia hạn thành công.",
                PaymentType.AdditionalCharge => $"Đơn #{booking.BookingId} đã thanh toán phụ phí thành công.",
                _ => $"Đơn #{booking.BookingId} đã thanh toán thành công."
            }
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "Pay",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Thanh toán mô phỏng {paymentType} cho đơn #{booking.BookingId}, số tiền {payment.Amount:N0} đồng, mã giao dịch {payment.TransactionCode}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> SubmitQrPaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .Include(item => item.Extensions)
            .FirstOrDefaultAsync(
                item => item.BookingId == bookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (paymentType == PaymentType.Refund)
        {
            return OperationResult.Failure("Khách hàng không thể tự thực hiện khoản hoàn tiền.");
        }

        if (paymentType == PaymentType.Rental && booking.Status != BookingStatus.PendingPayment)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán tiền thuê.");
        }

        if (paymentType == PaymentType.Extension &&
            (booking.Status != BookingStatus.Rented ||
             !booking.Extensions.Any(extension => extension.Status == BookingExtensionStatus.Approved)))
        {
            return OperationResult.Failure("Không có yêu cầu gia hạn đã duyệt đang chờ thanh toán.");
        }

        if (paymentType == PaymentType.AdditionalCharge && booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán phụ phí.");
        }

        if (booking.Payments.Any(payment =>
                payment.Type == paymentType && payment.Status == PaymentStatus.Paid))
        {
            return OperationResult.Failure("Khoản tiền này đã được thanh toán.");
        }

        if (booking.Payments.Any(payment =>
                payment.Type == paymentType && payment.Status == PaymentStatus.AwaitingConfirmation))
        {
            return OperationResult.Failure(
                "Khoản thanh toán này đã được gửi và đang chờ quản trị viên xác nhận.");
        }

        var payment = booking.Payments.FirstOrDefault(item =>
            item.Type == paymentType && item.Status == PaymentStatus.Pending);

        if (payment is null &&
            paymentType == PaymentType.AdditionalCharge &&
            booking.AdditionalAmount > 0)
        {
            payment = new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.AdditionalCharge,
                Amount = booking.AdditionalAmount,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending
            };

            booking.Payments.Add(payment);
        }

        if (payment is null)
        {
            return OperationResult.Failure("Không tìm thấy khoản thanh toán đang chờ.");
        }

        var previousReconciliationCode = payment.TransactionCode;

        payment.Method = PaymentMethods.BankQr;
        payment.Status = PaymentStatus.AwaitingConfirmation;
        payment.PaidAt = null;
        payment.TransactionCode = $"QRREQ{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã gửi xác nhận chuyển khoản",
            Message = paymentType switch
            {
                PaymentType.Rental => $"Tiền thuê của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",
                PaymentType.Extension => $"Tiền gia hạn của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",
                PaymentType.AdditionalCharge => $"Phụ phí của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",
                _ => $"Khoản thanh toán của đơn #{booking.BookingId} đang chờ SmartCar xác nhận."
            }
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "SubmitQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Khách báo đã chuyển khoản QR {paymentType} cho đơn #{booking.BookingId}, " +
            $"số tiền {payment.Amount:N0} đồng, mã yêu cầu đối soát {payment.TransactionCode}." +
            (string.IsNullOrWhiteSpace(previousReconciliationCode)
                ? string.Empty
                : $" Mã yêu cầu trước đó: {previousReconciliationCode}."),
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmQrPaymentAsync(
        int paymentId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var payment = await _dbContext.Payments
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Extensions)
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

        if (payment is null)
        {
            return OperationResult.Failure("Không tìm thấy giao dịch.");
        }

        if (payment.Status != PaymentStatus.AwaitingConfirmation ||
            payment.Method != PaymentMethods.BankQr)
        {
            return OperationResult.Failure("Giao dịch không ở trạng thái chờ xác nhận QR.");
        }

        if (payment.Type == PaymentType.Refund)
        {
            return OperationResult.Failure("Khoản hoàn tiền không được xác nhận bằng luồng thanh toán QR.");
        }

        var booking = payment.Booking;
        var confirmingCancelledRental =
            payment.Type == PaymentType.Rental &&
            booking.Status == BookingStatus.Cancelled;

        if (payment.Type == PaymentType.Rental)
        {
            if (booking.Status is not (BookingStatus.PendingPayment or BookingStatus.Cancelled))
            {
                return OperationResult.Failure("Đơn không còn ở trạng thái có thể xác nhận tiền thuê.");
            }
        }
        else if (payment.Type == PaymentType.Extension)
        {
            if (booking.Status != BookingStatus.Rented)
            {
                return OperationResult.Failure("Đơn không còn ở trạng thái đang thuê để xác nhận tiền gia hạn.");
            }

            var extension = booking.Extensions
                .Where(item => item.Status == BookingExtensionStatus.Approved)
                .OrderByDescending(item => item.RequestedAt)
                .FirstOrDefault();

            if (extension is null)
            {
                return OperationResult.Failure("Không còn yêu cầu gia hạn chờ thanh toán.");
            }

            extension.Status = BookingExtensionStatus.Paid;
            extension.PaidAt = DateTime.UtcNow;
        }
        else if (payment.Type == PaymentType.AdditionalCharge)
        {
            if (booking.Status != BookingStatus.PendingInspection)
            {
                return OperationResult.Failure("Đơn không còn ở trạng thái chờ thanh toán phụ phí.");
            }
        }

        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = $"QR{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        if (payment.Type == PaymentType.Rental && !confirmingCancelledRental)
        {
            booking.Status = BookingStatus.Paid;
        }

        if (confirmingCancelledRental)
        {
            var cancellationTime = booking.CancelledAt?.ToLocalTime() ?? DateTime.Now;
            var hoursBeforePickup = (booking.PickupDate - cancellationTime).TotalHours;

            decimal refundAmount;
            string refundReason;

            if (string.Equals(booking.CancelledBy, "Quản trị viên", StringComparison.OrdinalIgnoreCase))
            {
                refundAmount = payment.Amount;
                refundReason = "SmartCar chủ động hủy trước khi giao xe: hoàn 100% số tiền đã thanh toán.";
            }
            else if (hoursBeforePickup >= 48)
            {
                refundAmount = payment.Amount;
                refundReason = "Khách hủy trước thời gian nhận xe từ 48 giờ trở lên: hoàn 100%.";
            }
            else if (hoursBeforePickup >= 24)
            {
                refundAmount = Math.Round(payment.Amount * 0.5m, 0);
                refundReason = "Khách hủy trước thời gian nhận xe từ 24 đến dưới 48 giờ: hoàn 50%.";
            }
            else
            {
                refundAmount = 0;
                refundReason = "Khách hủy trước thời gian nhận xe dưới 24 giờ: không hoàn tiền.";
            }

            booking.RefundAmount = refundAmount;
            booking.RefundReason = refundReason;

            if (refundAmount > 0 &&
                !booking.Payments.Any(item => item.Type == PaymentType.Refund))
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
                Title = refundAmount > 0
                    ? "Khoản hoàn tiền đã được tạo"
                    : "Đã xác nhận khoản chuyển của đơn đã hủy",
                Message = refundAmount > 0
                    ? $"SmartCar đã xác nhận khoản chuyển {payment.Amount:N0} đồng của đơn #{booking.BookingId}. " +
                      $"Theo chính sách hủy, khoản hoàn {refundAmount:N0} đồng đã được tạo và đang chờ xử lý."
                    : $"SmartCar đã xác nhận khoản chuyển {payment.Amount:N0} đồng của đơn #{booking.BookingId}. {refundReason}"
            });
        }
        else
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Thanh toán đã được xác nhận",
                Message = payment.Type switch
                {
                    PaymentType.Rental => $"SmartCar đã xác nhận tiền thuê {payment.Amount:N0} đồng của đơn #{booking.BookingId}.",
                    PaymentType.Extension => $"SmartCar đã xác nhận tiền gia hạn {payment.Amount:N0} đồng của đơn #{booking.BookingId}.",
                    PaymentType.AdditionalCharge => $"SmartCar đã xác nhận phụ phí {payment.Amount:N0} đồng của đơn #{booking.BookingId}.",
                    _ => $"SmartCar đã xác nhận khoản thanh toán {payment.Amount:N0} đồng của đơn #{booking.BookingId}."
                }
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            confirmingCancelledRental ? "ConfirmCancelledBookingPayment" : "ConfirmQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            confirmingCancelledRental
                ? $"Xác nhận chuyển khoản QR tiền thuê cho đơn đã hủy #{booking.BookingId}, {payment.Amount:N0} đồng; hoàn dự kiến {booking.RefundAmount:N0} đồng."
                : $"Xác nhận chuyển khoản QR {payment.Type} cho đơn #{booking.BookingId}, {payment.Amount:N0} đồng.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RejectQrPaymentAsync(
        int paymentId,
        string adminId,
        string rejectionReason,
        CancellationToken cancellationToken = default)
    {
        var normalizedReason = rejectionReason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedReason))
        {
            return OperationResult.Failure("Vui lòng nhập lý do chưa tìm thấy giao dịch.");
        }

        if (normalizedReason.Length > 500)
        {
            return OperationResult.Failure("Lý do chưa tìm thấy giao dịch không được vượt quá 500 ký tự.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var payment = await _dbContext.Payments
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

        if (payment is null)
        {
            return OperationResult.Failure("Không tìm thấy giao dịch.");
        }

        if (payment.Status != PaymentStatus.AwaitingConfirmation ||
            payment.Method != PaymentMethods.BankQr)
        {
            return OperationResult.Failure("Giao dịch không ở trạng thái chờ xác nhận QR.");
        }

        var reconciliationCode = payment.TransactionCode;

        payment.Status = PaymentStatus.Pending;
        payment.Method = PaymentMethods.NotSelected;
        payment.PaidAt = null;
        // Giữ nguyên TransactionCode để không xóa dấu vết lần khách đã báo chuyển khoản.

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "SmartCar chưa tìm thấy giao dịch chuyển khoản",
            Message = $"SmartCar chưa đối soát được khoản {payment.Amount:N0} đồng của đơn #{payment.BookingId}. " +
                      $"Mã yêu cầu: {reconciliationCode ?? "không có"}. Lý do đối soát: {normalizedReason}. " +
                      "Khoản thanh toán được trả về trạng thái chờ thanh toán; lịch sử đối soát vẫn được lưu."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "QrPaymentNotFound",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Admin chưa tìm thấy chuyển khoản QR của đơn #{payment.BookingId}, số tiền {payment.Amount:N0} đồng. " +
            $"Mã yêu cầu đối soát: {reconciliationCode ?? "không có"}. Lý do: {normalizedReason}. " +
            "Dấu vết giao dịch được giữ lại để kiểm tra sau.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmRefundAsync(
        int paymentId,
        string adminId,
        string? transactionCode,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var payment = await _dbContext.Payments
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.PaymentId == paymentId, cancellationToken);

        if (payment is null)
        {
            return OperationResult.Failure("Không tìm thấy khoản hoàn tiền.");
        }

        if (payment.Type != PaymentType.Refund)
        {
            return OperationResult.Failure("Giao dịch này không phải khoản hoàn tiền.");
        }

        if (payment.Status != PaymentStatus.AwaitingRefund)
        {
            return OperationResult.Failure("Khoản hoàn tiền không còn ở trạng thái chờ xử lý.");
        }

        payment.Status = PaymentStatus.Refunded;
        payment.Method = PaymentMethods.BankTransferRefund;
        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = string.IsNullOrWhiteSpace(transactionCode)
            ? $"RF{DateTime.UtcNow:yyyyMMddHHmmssfff}{payment.BookingId}"
            : transactionCode.Trim();

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "Hoàn tiền thành công",
            Message = $"SmartCar đã hoàn {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. Mã giao dịch: {payment.TransactionCode}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Refund",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Hoàn {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. Mã giao dịch: {payment.TransactionCode}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }
}