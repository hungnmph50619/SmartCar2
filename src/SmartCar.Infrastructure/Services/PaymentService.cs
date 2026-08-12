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

    public PaymentService(
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<AdminPaymentListItemDto>> GetAdminPaymentsAsync(
        PaymentStatus? status = null,
        PaymentType? type = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Payments
            .AsNoTracking()
            .AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(payment =>
                payment.Status == status.Value);
        }

        if (type.HasValue)
        {
            query = query.Where(payment =>
                payment.Type == type.Value);
        }

        return await query
            .OrderByDescending(payment => payment.PaymentId)
            .Select(payment => new AdminPaymentListItemDto(
                payment.PaymentId,
                payment.BookingId,
                payment.Booking.CustomerId,

                _dbContext.Users
                    .Where(user =>
                        user.Id == payment.Booking.CustomerId)
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

    // ============================================================
    // THANH TOÁN MÔ PHỎNG
    // ============================================================

    public async Task<OperationResult> SimulatePaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .Include(item => item.Extensions)
            .FirstOrDefaultAsync(
                item =>
                    item.BookingId == bookingId &&
                    item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy đơn thuê của bạn.");
        }

        // Không cho khách tự hoàn tiền.
        if (paymentType == PaymentType.Refund)
        {
            return OperationResult.Failure(
                "Khoản hoàn tiền chỉ do hệ thống và quản trị viên xử lý.");
        }

        // ========================================================
        // KIỂM TRA TRẠNG THÁI ĐƠN
        // ========================================================

        if (paymentType == PaymentType.Rental &&
            booking.Status != BookingStatus.PendingPayment)
        {
            return OperationResult.Failure(
                "Đơn không ở trạng thái chờ thanh toán tiền thuê.");
        }

        if (paymentType == PaymentType.Extension &&
            (booking.Status != BookingStatus.Rented ||
             !booking.Extensions.Any(extension =>
                 extension.Status ==
                 BookingExtensionStatus.Approved)))
        {
            return OperationResult.Failure(
                "Không có yêu cầu gia hạn đã duyệt đang chờ thanh toán.");
        }

        if (paymentType == PaymentType.AdditionalCharge &&
            booking.Status != BookingStatus.PendingInspection)
        {
            return OperationResult.Failure(
                "Đơn không ở trạng thái chờ thanh toán phụ phí.");
        }

        // ========================================================
        // KHÔNG CHO THANH TOÁN MÔ PHỎNG NẾU QR ĐANG CHỜ ADMIN
        // ========================================================

        var awaitingQrConfirmation =
            booking.Payments.Any(item =>
                item.Type == paymentType &&
                item.Status ==
                    PaymentStatus.AwaitingConfirmation);

        if (awaitingQrConfirmation)
        {
            return OperationResult.Failure(
                "Khoản thanh toán đang chờ quản trị viên xác nhận chuyển khoản QR. " +
                "Không thể thanh toán lại bằng mô phỏng.");
        }

        // ========================================================
        // TÌM PAYMENT PENDING
        // ========================================================

        var payment = booking.Payments
            .FirstOrDefault(item =>
                item.Type == paymentType &&
                item.Status == PaymentStatus.Pending);

        // Trường hợp phụ phí chưa có Payment thì tạo.
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
            var alreadyPaid =
                booking.Payments.Any(item =>
                    item.Type == paymentType &&
                    item.Status == PaymentStatus.Paid);

            return alreadyPaid
                ? OperationResult.Failure(
                    "Khoản tiền này đã được thanh toán.")
                : OperationResult.Failure(
                    "Không tìm thấy khoản thanh toán phù hợp.");
        }

<<<<<<< HEAD
        // ========================================================
        // KIỂM TRA KHUYẾN MÃI KHI THANH TOÁN TIỀN THUÊ
        // ========================================================

        if (paymentType == PaymentType.Rental &&
            !string.IsNullOrWhiteSpace(
                booking.PromotionCode))
        {
            var now = DateTime.Now;

            var promotion =
                await _dbContext.Promotions
                    .FirstOrDefaultAsync(
                        item =>
                            item.Code ==
                                booking.PromotionCode &&
                            item.IsActive &&
                            item.StartAt <= now &&
                            item.EndAt >= now,
                        cancellationToken);

            if (promotion is null)
            {
                return OperationResult.Failure(
                    "Mã khuyến mãi không còn hiệu lực. " +
                    "Vui lòng gỡ mã hoặc chọn mã khác.");
            }

            if (promotion.UsageLimit.HasValue &&
                promotion.UsedCount >=
                promotion.UsageLimit.Value)
            {
                return OperationResult.Failure(
                    "Mã khuyến mãi vừa hết lượt sử dụng. " +
                    "Vui lòng gỡ mã hoặc chọn mã khác.");
            }

            promotion.UsedCount++;
        }

        // ========================================================
        // KHÁCH CHỌN THANH TOÁN MÔ PHỎNG
        // ========================================================

        payment.Method =
            PaymentMethods.Simulation;

        payment.Status =
            PaymentStatus.Paid;

        payment.PaidAt =
            DateTime.UtcNow;

        payment.TransactionCode =
            $"SC{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        // ========================================================
        // XỬ LÝ THEO LOẠI PAYMENT
        // ========================================================
=======
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = $"SC{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";
>>>>>>> origin/hung-dev

        if (paymentType == PaymentType.Rental)
        {
            booking.Status =
                BookingStatus.Paid;
        }
        else if (paymentType ==
                 PaymentType.Extension)
        {
            var extension =
                booking.Extensions
                    .Where(item =>
                        item.Status ==
                        BookingExtensionStatus.Approved)
                    .OrderByDescending(item =>
                        item.RequestedAt)
                    .FirstOrDefault();

            if (extension is null)
            {
                return OperationResult.Failure(
                    "Không tìm thấy yêu cầu gia hạn đang chờ thanh toán.");
            }

            extension.Status =
                BookingExtensionStatus.Paid;

            extension.PaidAt =
                DateTime.UtcNow;
        }

        // ========================================================
        // THÔNG BÁO
        // ========================================================

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    booking.CustomerId,

                Title =
                    "Thanh toán thành công",

                Message = paymentType switch
                {
                    PaymentType.Rental =>
                        $"Đơn #{booking.BookingId} đã thanh toán tiền thuê thành công.",

                    PaymentType.Extension =>
                        $"Đơn #{booking.BookingId} đã thanh toán tiền gia hạn thành công.",

                    PaymentType.AdditionalCharge =>
                        $"Đơn #{booking.BookingId} đã thanh toán phụ phí thành công.",

                    _ =>
                        $"Đơn #{booking.BookingId} đã thanh toán thành công."
                }
            });

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "Pay",
            nameof(Payment),
            payment.PaymentId.ToString(),

            $"Thanh toán mô phỏng {paymentType} " +
            $"cho đơn #{booking.BookingId}, " +
            $"số tiền {payment.Amount:N0} đồng, " +
            $"mã giao dịch {payment.TransactionCode}.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    // ============================================================
    // KHÁCH BÁO ĐÃ CHUYỂN KHOẢN QR
    // ============================================================

    public async Task<OperationResult> SubmitQrPaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        var booking =
            await _dbContext.Bookings
                .Include(item =>
                    item.Payments)
                .Include(item =>
                    item.Extensions)
                .FirstOrDefaultAsync(
                    item =>
                        item.BookingId == bookingId &&
                        item.CustomerId == customerId,
                    cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy đơn thuê của bạn.");
        }

        // Khách không thể tự thực hiện refund.
        if (paymentType ==
            PaymentType.Refund)
        {
            return OperationResult.Failure(
                "Khách hàng không thể tự thực hiện khoản hoàn tiền.");
        }

        // ========================================================
        // KIỂM TRA TRẠNG THÁI ĐƠN
        // ========================================================

        if (paymentType ==
                PaymentType.Rental &&
            booking.Status !=
                BookingStatus.PendingPayment)
        {
            return OperationResult.Failure(
                "Đơn không ở trạng thái chờ thanh toán tiền thuê.");
        }

        if (paymentType ==
                PaymentType.Extension &&
            (booking.Status !=
                 BookingStatus.Rented ||
             !booking.Extensions.Any(
                 extension =>
                     extension.Status ==
                     BookingExtensionStatus.Approved)))
        {
            return OperationResult.Failure(
                "Không có yêu cầu gia hạn đã duyệt đang chờ thanh toán.");
        }

        if (paymentType ==
                PaymentType.AdditionalCharge &&
            booking.Status !=
                BookingStatus.PendingInspection)
        {
            return OperationResult.Failure(
                "Đơn không ở trạng thái chờ thanh toán phụ phí.");
        }

        // ========================================================
        // ĐÃ THANH TOÁN RỒI THÌ KHÔNG GỬI QR LẠI
        // ========================================================

        var alreadyPaid =
            booking.Payments.Any(payment =>
                payment.Type == paymentType &&
                payment.Status ==
                    PaymentStatus.Paid);

        if (alreadyPaid)
        {
            return OperationResult.Failure(
                "Khoản tiền này đã được thanh toán.");
        }

        // ========================================================
        // ĐANG CHỜ QR RỒI THÌ KHÔNG GỬI LẠI
        // ========================================================

        var alreadyWaiting =
            booking.Payments.Any(payment =>
                payment.Type == paymentType &&
                payment.Status ==
                    PaymentStatus.AwaitingConfirmation);

        if (alreadyWaiting)
        {
            return OperationResult.Failure(
                "Khoản thanh toán này đã được gửi " +
                "và đang chờ quản trị viên xác nhận.");
        }

        // ========================================================
        // TÌM PAYMENT PENDING
        // ========================================================

        var payment =
            booking.Payments.FirstOrDefault(
                item =>
                    item.Type == paymentType &&
                    item.Status ==
                        PaymentStatus.Pending);

        // Nếu phụ phí chưa có Payment thì tạo.
        if (payment is null &&
            paymentType ==
                PaymentType.AdditionalCharge &&
            booking.AdditionalAmount > 0)
        {
            payment = new Payment
            {
                BookingId =
                    booking.BookingId,

                Type =
                    PaymentType.AdditionalCharge,

                Amount =
                    booking.AdditionalAmount,

                Method =
                    PaymentMethods.NotSelected,

                Status =
                    PaymentStatus.Pending
            };

            booking.Payments.Add(
                payment);
        }

        if (payment is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy khoản thanh toán đang chờ.");
        }

        // ========================================================
        // CHUYỂN PAYMENT SANG CHỜ ADMIN XÁC NHẬN
        // ========================================================

        payment.Method =
            PaymentMethods.BankQr;

        payment.Status =
            PaymentStatus.AwaitingConfirmation;

        payment.PaidAt =
            null;

        payment.TransactionCode =
            $"QRREQ{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        // ========================================================
        // THÔNG BÁO
        // ========================================================

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    booking.CustomerId,

                Title =
                    "Đã gửi xác nhận chuyển khoản",

                Message = paymentType switch
                {
                    PaymentType.Rental =>
                        $"Tiền thuê của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",

                    PaymentType.Extension =>
                        $"Tiền gia hạn của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",

                    PaymentType.AdditionalCharge =>
                        $"Phụ phí của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",

                    _ =>
                        $"Khoản thanh toán của đơn #{booking.BookingId} đang chờ SmartCar xác nhận."
                }
            });

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "SubmitQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),

            $"Khách báo đã chuyển khoản QR {paymentType} " +
            $"cho đơn #{booking.BookingId}, " +
            $"số tiền {payment.Amount:N0} đồng.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    // ============================================================
    // ADMIN XÁC NHẬN ĐÃ NHẬN TIỀN QR
    // ============================================================

    public async Task<OperationResult> ConfirmQrPaymentAsync(
        int paymentId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        var payment =
            await _dbContext.Payments
                .Include(item =>
                    item.Booking)
                .ThenInclude(booking =>
                    booking.Extensions)
                .FirstOrDefaultAsync(
                    item =>
                        item.PaymentId ==
                        paymentId,
                    cancellationToken);

        if (payment is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy giao dịch.");
        }

        if (payment.Status !=
                PaymentStatus.AwaitingConfirmation ||
            payment.Method !=
                PaymentMethods.BankQr)
        {
            return OperationResult.Failure(
                "Giao dịch không ở trạng thái chờ xác nhận QR.");
        }

        if (payment.Type ==
            PaymentType.Refund)
        {
            return OperationResult.Failure(
                "Khoản hoàn tiền không được xác nhận bằng luồng thanh toán QR.");
        }

        var booking =
            payment.Booking;

        // ========================================================
        // TIỀN THUÊ
        // ========================================================

        if (payment.Type ==
            PaymentType.Rental)
        {
            if (booking.Status !=
                BookingStatus.PendingPayment)
            {
                return OperationResult.Failure(
                    "Đơn không còn ở trạng thái chờ thanh toán tiền thuê.");
            }

            if (!string.IsNullOrWhiteSpace(
                booking.PromotionCode))
            {
                var promotion =
                    await _dbContext.Promotions
                        .FirstOrDefaultAsync(
                            item =>
                                item.Code ==
                                booking.PromotionCode,
                            cancellationToken);

                if (promotion is null)
                {
                    return OperationResult.Failure(
                        "Không tìm thấy mã khuyến mãi đã áp dụng cho đơn.");
                }

                if (promotion.UsageLimit.HasValue &&
                    promotion.UsedCount >=
                    promotion.UsageLimit.Value)
                {
                    return OperationResult.Failure(
                        "Mã khuyến mãi đã hết lượt sử dụng. " +
                        "Cần xử lý mã trước khi xác nhận khoản thanh toán này.");
                }

                promotion.UsedCount++;
            }
        }

        // ========================================================
        // TIỀN GIA HẠN
        // ========================================================

        else if (payment.Type ==
                 PaymentType.Extension)
        {
            if (booking.Status !=
                BookingStatus.Rented)
            {
                return OperationResult.Failure(
                    "Đơn không còn ở trạng thái đang thuê " +
                    "để xác nhận tiền gia hạn.");
            }

            var extension =
                booking.Extensions
                    .Where(item =>
                        item.Status ==
                        BookingExtensionStatus.Approved)
                    .OrderByDescending(item =>
                        item.RequestedAt)
                    .FirstOrDefault();

            if (extension is null)
            {
                return OperationResult.Failure(
                    "Không còn yêu cầu gia hạn chờ thanh toán.");
            }

            extension.Status =
                BookingExtensionStatus.Paid;

            extension.PaidAt =
                DateTime.UtcNow;
        }

        // ========================================================
        // PHỤ PHÍ
        // ========================================================

        else if (payment.Type ==
                 PaymentType.AdditionalCharge)
        {
            if (booking.Status !=
                BookingStatus.PendingInspection)
            {
                return OperationResult.Failure(
                    "Đơn không còn ở trạng thái chờ thanh toán phụ phí.");
            }
        }

        // ========================================================
        // PAYMENT THÀNH CÔNG
        // ========================================================

        payment.Status =
            PaymentStatus.Paid;

        payment.PaidAt =
            DateTime.UtcNow;

        payment.TransactionCode =
            $"QR{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        // Tiền thuê thành công thì Booking chuyển Paid.
        if (payment.Type ==
            PaymentType.Rental)
        {
            booking.Status =
                BookingStatus.Paid;
        }

        // ========================================================
        // THÔNG BÁO
        // ========================================================

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    booking.CustomerId,

                Title =
                    "Thanh toán đã được xác nhận",

                Message = payment.Type switch
                {
                    PaymentType.Rental =>
                        $"SmartCar đã xác nhận tiền thuê {payment.Amount:N0} đồng " +
                        $"của đơn #{booking.BookingId}.",

                    PaymentType.Extension =>
                        $"SmartCar đã xác nhận tiền gia hạn {payment.Amount:N0} đồng " +
                        $"của đơn #{booking.BookingId}.",

                    PaymentType.AdditionalCharge =>
                        $"SmartCar đã xác nhận phụ phí {payment.Amount:N0} đồng " +
                        $"của đơn #{booking.BookingId}.",

                    _ =>
                        $"SmartCar đã xác nhận khoản thanh toán {payment.Amount:N0} đồng " +
                        $"của đơn #{booking.BookingId}."
                }
            });

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ConfirmQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),

            $"Xác nhận chuyển khoản QR {payment.Type} " +
            $"cho đơn #{booking.BookingId}, " +
            $"{payment.Amount:N0} đồng.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    // ============================================================
    // ADMIN BÁO CHƯA NHẬN ĐƯỢC TIỀN QR
    // ============================================================

    public async Task<OperationResult> RejectQrPaymentAsync(
        int paymentId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        var payment =
            await _dbContext.Payments
                .Include(item =>
                    item.Booking)
                .FirstOrDefaultAsync(
                    item =>
                        item.PaymentId ==
                        paymentId,
                    cancellationToken);

        if (payment is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy giao dịch.");
        }

        if (payment.Status !=
                PaymentStatus.AwaitingConfirmation ||
            payment.Method !=
                PaymentMethods.BankQr)
        {
            return OperationResult.Failure(
                "Giao dịch không ở trạng thái chờ xác nhận QR.");
        }

        // Đưa Payment về trạng thái ban đầu.
        payment.Status =
            PaymentStatus.Pending;

        payment.Method =
            PaymentMethods.NotSelected;

        payment.PaidAt =
            null;

        payment.TransactionCode =
            null;

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    payment.Booking.CustomerId,

                Title =
                    "Chưa xác nhận được chuyển khoản",

                Message =
                    $"SmartCar chưa tìm thấy giao dịch của đơn " +
                    $"#{payment.BookingId}. " +
                    "Vui lòng kiểm tra và thanh toán lại."
            });

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "RejectQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),

            $"Chưa xác nhận được chuyển khoản QR " +
            $"của đơn #{payment.BookingId}.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    // ============================================================
    // ADMIN XÁC NHẬN ĐÃ HOÀN TIỀN
    // ============================================================

    public async Task<OperationResult> ConfirmRefundAsync(
        int paymentId,
        string adminId,
        string? transactionCode,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        var payment =
            await _dbContext.Payments
                .Include(item =>
                    item.Booking)
                .FirstOrDefaultAsync(
                    item =>
                        item.PaymentId ==
                        paymentId,
                    cancellationToken);

        if (payment is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy khoản hoàn tiền.");
        }

        if (payment.Type !=
            PaymentType.Refund)
        {
            return OperationResult.Failure(
                "Giao dịch này không phải khoản hoàn tiền.");
        }

        if (payment.Status !=
            PaymentStatus.AwaitingRefund)
        {
            return OperationResult.Failure(
                "Khoản hoàn tiền không còn ở trạng thái chờ xử lý.");
        }

        // Chuyển refund thành hoàn tất.
        payment.Status =
            PaymentStatus.Refunded;

        payment.Method =
            PaymentMethods.BankTransferRefund;

        payment.PaidAt =
            DateTime.UtcNow;

        // Nếu admin nhập mã ngân hàng thì dùng mã đó.
        // Nếu không nhập thì hệ thống sinh mã demo.
        payment.TransactionCode =
            string.IsNullOrWhiteSpace(
                transactionCode)

                ? $"RF{DateTime.UtcNow:yyyyMMddHHmmssfff}{payment.BookingId}"

                : transactionCode.Trim();

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    payment.Booking.CustomerId,

                Title =
                    "Hoàn tiền thành công",

                Message =
                    $"SmartCar đã hoàn {payment.Amount:N0} đồng " +
                    $"cho đơn #{payment.BookingId}. " +
                    $"Mã giao dịch: {payment.TransactionCode}."
            });

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Refund",
            nameof(Payment),
            payment.PaymentId.ToString(),

            $"Hoàn {payment.Amount:N0} đồng " +
            $"cho đơn #{payment.BookingId}. " +
            $"Mã giao dịch: {payment.TransactionCode}.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }
}