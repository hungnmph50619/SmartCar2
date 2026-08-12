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
        _dbContext =
            dbContext;

        _auditService =
            auditService;
    }

    public async Task<IReadOnlyList<AdminPaymentListItemDto>>
        GetAdminPaymentsAsync(
            PaymentStatus? status = null,
            PaymentType? type = null,
            CancellationToken cancellationToken = default)
    {
        var query =
            _dbContext.Payments
                .AsNoTracking()

                // Tiền cọc là một phần của giao dịch
                // thanh toán trước khi nhận xe.
                // Không hiện thành dòng riêng để admin
                // không xác nhận hai lần.
                .Where(payment =>
                    payment.Type !=
                    PaymentType.Deposit)

                .AsQueryable();

        if (status.HasValue)
        {
            query =
                query.Where(payment =>
                    payment.Status ==
                    status.Value);
        }

        if (type.HasValue)
        {
            query =
                query.Where(payment =>
                    payment.Type ==
                    type.Value);
        }

        return await query
            .OrderByDescending(payment =>
                payment.PaymentId)

            .Select(payment =>
                new AdminPaymentListItemDto(
                    payment.PaymentId,
                    payment.BookingId,
                    payment.Booking.CustomerId,

                    _dbContext.Users
                        .Where(user =>
                            user.Id ==
                            payment.Booking.CustomerId)
                        .Select(user =>
                            user.FullName)
                        .FirstOrDefault()
                    ?? string.Empty,

                    payment.Booking.Vehicle.VehicleName,

                    payment.Booking.Vehicle.LicensePlate,

                    payment.Type,

                    payment.Type ==
                    PaymentType.Rental

                        ? payment.Amount
                          +
                          payment.Booking.Payments
                              .Where(deposit =>
                                  deposit.Type ==
                                  PaymentType.Deposit)
                              .Sum(deposit =>
                                  deposit.Amount)

                        : payment.Amount,

                    payment.Method,
                    payment.Status,
                    payment.PaidAt,
                    payment.TransactionCode))

            .ToListAsync(
                cancellationToken);
    }

    public async Task<OperationResult>
        SubmitQrPaymentAsync(
            int bookingId,
            string customerId,
            PaymentType paymentType,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(
                customerId))
        {
            return OperationResult.Failure(
                "Không xác định được khách hàng.");
        }

        if (!Enum.IsDefined(
                paymentType) ||
            paymentType ==
            PaymentType.Refund)
        {
            return OperationResult.Failure(
                "Loại thanh toán không hợp lệ.");
        }

        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
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
                        item.BookingId ==
                            bookingId &&
                        item.CustomerId ==
                            customerId,

                    cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy đơn thuê của bạn.");
        }

        // Tiền cọc luôn đi cùng giao dịch Rental.
        if (paymentType ==
            PaymentType.Deposit)
        {
            return OperationResult.Failure(
                "Tiền cọc không chuyển riêng. " +
                "Vui lòng dùng một lần chuyển khoản tổng tiền thuê/phí giao + tiền cọc.");
        }

        var stateError =
            ValidateCustomerPaymentState(
                booking,
                paymentType);

        if (stateError is not null)
        {
            return OperationResult.Failure(
                stateError);
        }

        var awaitingPayment =
            booking.Payments
                .FirstOrDefault(payment =>
                    payment.Type ==
                        paymentType &&
                    payment.Status ==
                        PaymentStatus.AwaitingConfirmation);

        if (awaitingPayment is not null)
        {
            return OperationResult.Failure(
                "Khoản thanh toán này đã được gửi và đang chờ SmartCar xác nhận.");
        }

        var payment =
            booking.Payments
                .FirstOrDefault(item =>
                    item.Type ==
                        paymentType &&
                    item.Status ==
                        PaymentStatus.Pending);

        // Nếu chưa tồn tại payment phụ phí
        // thì tạo tự động.
        if (payment is null &&
            paymentType ==
                PaymentType.AdditionalCharge &&
            booking.AdditionalAmount > 0)
        {
            payment =
                new Payment
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
            var alreadyPaid =
                booking.Payments.Any(item =>
                    item.Type ==
                        paymentType &&
                    item.Status ==
                        PaymentStatus.Paid);

            return alreadyPaid

                ? OperationResult.Failure(
                    "Khoản tiền này đã được thanh toán.")

                : OperationResult.Failure(
                    "Không tìm thấy khoản thanh toán phù hợp.");
        }

        // ============================================================
        // SỬA PHÍ GIAO XE
        // ============================================================

        if (paymentType ==
                PaymentType.Rental &&
            booking.PickupMethod ==
                VehiclePickupMethod.Delivery)
        {
            var storedDeliveryFee =
                Math.Max(
                    0m,
                    booking.TotalAmount
                    - booking.RentalAmount
                    - booking.AdditionalAmount);

            // Hỗ trợ những đơn cũ từng bị lưu thiếu phí giao.
            if (storedDeliveryFee <= 0m &&
                booking.DeliveryLatitude.HasValue &&
                booking.DeliveryLongitude.HasValue)
            {
                storedDeliveryFee =
                    RentalPolicy.CalculateDeliveryFee(
                        booking.PickupMethod,
                        booking.DeliveryLatitude,
                        booking.DeliveryLongitude);
            }

            var expectedRentalPayment =
                booking.RentalAmount
                + storedDeliveryFee;

            payment.Amount =
                expectedRentalPayment;

            booking.TotalAmount =
                booking.RentalAmount
                + storedDeliveryFee
                + booking.AdditionalAmount;
        }

        // ============================================================
        // KHÁCH BÁO ĐÃ CHUYỂN
        //
        // KHÔNG chuyển Paid ở đây.
        // Phải chờ admin xác nhận.
        // ============================================================

        payment.Method =
            PaymentMethods.BankQr;

        payment.Status =
            PaymentStatus.AwaitingConfirmation;

        payment.PaidAt =
            null;

        payment.TransactionCode =
            null;

        Payment? bundledDeposit =
            null;

        if (paymentType ==
                PaymentType.Rental &&
            booking.DepositAmount > 0)
        {
            bundledDeposit =
                booking.Payments
                    .FirstOrDefault(item =>
                        item.Type ==
                        PaymentType.Deposit);

            // Dữ liệu cũ chưa có record cọc.
            if (bundledDeposit is null)
            {
                bundledDeposit =
                    new Payment
                    {
                        BookingId =
                            booking.BookingId,

                        Type =
                            PaymentType.Deposit,

                        Amount =
                            booking.DepositAmount,

                        Method =
                            PaymentMethods.BankQr,

                        Status =
                            PaymentStatus.AwaitingConfirmation
                    };

                booking.Payments.Add(
                    bundledDeposit);
            }

            else if (
                bundledDeposit.Status !=
                PaymentStatus.Paid)
            {
                bundledDeposit.Amount =
                    booking.DepositAmount;

                bundledDeposit.Method =
                    PaymentMethods.BankQr;

                bundledDeposit.Status =
                    PaymentStatus.AwaitingConfirmation;

                bundledDeposit.PaidAt =
                    null;

                bundledDeposit.TransactionCode =
                    null;
            }
        }

        var submittedAmount =
            payment.Amount
            +
            (bundledDeposit?.Amount ?? 0m);

        // Thông báo admin.
        await NotifyAdminsAsync(
            "Có giao dịch QR chờ xác nhận",

            paymentType ==
            PaymentType.Rental

                ? $"Đơn #{booking.BookingId} vừa báo đã chuyển một lần " +
                  $"{submittedAmount:N0} đồng gồm tiền thuê/phí giao và tiền cọc."

                : $"Đơn #{booking.BookingId} vừa báo đã chuyển " +
                  $"{submittedAmount:N0} đồng cho khoản " +
                  $"{GetPaymentLabel(payment.Type)}.",

            cancellationToken);

        await _dbContext
            .SaveChangesAsync(
                cancellationToken);

        await transaction
            .CommitAsync(
                cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "SubmitQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),

            paymentType ==
            PaymentType.Rental

                ? $"Khách báo đã chuyển QR gộp tiền thuê/phí giao + cọc " +
                  $"cho đơn #{booking.BookingId}, {submittedAmount:N0} đồng."

                : $"Khách báo đã chuyển QR " +
                  $"{GetPaymentLabel(payment.Type)} cho đơn " +
                  $"#{booking.BookingId}, {submittedAmount:N0} đồng.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult>
        ConfirmQrPaymentAsync(
            int paymentId,
            string adminId,
            CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

        var payment =
            await _dbContext.Payments

                .Include(item =>
                    item.Booking)
                    .ThenInclude(booking =>
                        booking.Payments)

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

        var booking =
            payment.Booking;

        var stateError =
            ValidateAdminConfirmationState(
                booking,
                payment.Type);

        if (stateError is not null)
        {
            return OperationResult.Failure(
                stateError);
        }

        var paidAt =
            DateTime.UtcNow;

        var transactionCode =
            $"QR{paidAt:yyyyMMddHHmmssfff}{booking.BookingId}";

        payment.Status =
            PaymentStatus.Paid;

        payment.PaidAt =
            paidAt;

        payment.TransactionCode =
            transactionCode;

        Payment? bundledDeposit =
            null;

        // ============================================================
        // XÁC NHẬN TIỀN THUÊ + CỌC
        // ============================================================

        if (payment.Type ==
            PaymentType.Rental)
        {
            bundledDeposit =
                booking.Payments
                    .FirstOrDefault(item =>
                        item.Type ==
                        PaymentType.Deposit);

            if (booking.DepositAmount > 0)
            {
                if (bundledDeposit is null)
                {
                    bundledDeposit =
                        new Payment
                        {
                            BookingId =
                                booking.BookingId,

                            Type =
                                PaymentType.Deposit,

                            Amount =
                                booking.DepositAmount
                        };

                    booking.Payments.Add(
                        bundledDeposit);
                }

                bundledDeposit.Amount =
                    booking.DepositAmount;

                bundledDeposit.Method =
                    PaymentMethods.BankQr;

                bundledDeposit.Status =
                    PaymentStatus.Paid;

                bundledDeposit.PaidAt =
                    paidAt;

                bundledDeposit.TransactionCode =
                    transactionCode;
            }

            // ========================================================
            // DÒNG QUAN TRỌNG NHẤT
            //
            // Admin xác nhận tiền -> Booking chuyển Paid.
            // Sau đó AdminBookings sẽ hiện nút Xe đã sẵn sàng.
            // ========================================================

            booking.Status =
                BookingStatus.Paid;
        }

        // ============================================================
        // GIA HẠN
        // ============================================================

        else if (payment.Type ==
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
                    "Không còn yêu cầu gia hạn chờ thanh toán.");
            }

            extension.Status =
                BookingExtensionStatus.Paid;

            extension.PaidAt =
                paidAt;
        }

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    booking.CustomerId,

                Title =
                    "Thanh toán đã được xác nhận",

                Message =
                    payment.Type switch
                    {
                        PaymentType.Rental =>
                            $"SmartCar đã xác nhận một lần chuyển tiền thuê/phí giao " +
                            $"và tiền cọc tổng " +
                            $"{(payment.Amount + (bundledDeposit?.Amount ?? 0m)):N0} đồng " +
                            $"của đơn #{booking.BookingId}. " +
                            $"Đơn đã sẵn sàng cho bước chuẩn bị giao xe.",

                        PaymentType.Deposit =>
                            $"SmartCar đã xác nhận tiền cọc " +
                            $"{payment.Amount:N0} đồng của đơn #{booking.BookingId}.",

                        PaymentType.Extension =>
                            $"SmartCar đã xác nhận tiền gia hạn " +
                            $"{payment.Amount:N0} đồng của đơn #{booking.BookingId}.",

                        PaymentType.AdditionalCharge =>
                            $"SmartCar đã xác nhận phụ phí " +
                            $"{payment.Amount:N0} đồng của đơn #{booking.BookingId}.",

                        _ =>
                            $"SmartCar đã xác nhận khoản thanh toán " +
                            $"{payment.Amount:N0} đồng của đơn #{booking.BookingId}."
                    }
            });

        await _dbContext
            .SaveChangesAsync(
                cancellationToken);

        await transaction
            .CommitAsync(
                cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ConfirmQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),

            payment.Type ==
            PaymentType.Rental

                ? $"Xác nhận QR gộp tiền thuê/phí giao + cọc " +
                  $"cho đơn #{booking.BookingId}, " +
                  $"{(payment.Amount + (bundledDeposit?.Amount ?? 0m)):N0} đồng."

                : $"Xác nhận QR {GetPaymentLabel(payment.Type)} " +
                  $"cho đơn #{booking.BookingId}, {payment.Amount:N0} đồng.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult>
        RejectQrPaymentAsync(
            int paymentId,
            string adminId,
            CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

        var payment =
            await _dbContext.Payments

                .Include(item =>
                    item.Booking)
                    .ThenInclude(booking =>
                        booking.Payments)

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

        payment.Status =
            PaymentStatus.Pending;

        payment.Method =
            PaymentMethods.NotSelected;

        payment.PaidAt =
            null;

        payment.TransactionCode =
            null;

        // Nếu Rental bị từ chối đối soát
        // thì cọc cũng quay về Pending.
        if (payment.Type ==
            PaymentType.Rental)
        {
            var bundledDeposit =
                payment.Booking.Payments
                    .FirstOrDefault(item =>
                        item.Type ==
                            PaymentType.Deposit &&
                        item.Status ==
                            PaymentStatus.AwaitingConfirmation);

            if (bundledDeposit is not null)
            {
                bundledDeposit.Status =
                    PaymentStatus.Pending;

                bundledDeposit.Method =
                    PaymentMethods.NotSelected;

                bundledDeposit.PaidAt =
                    null;

                bundledDeposit.TransactionCode =
                    null;
            }
        }

        _dbContext.Notifications.Add(
            new Notification
            {
                UserId =
                    payment.Booking.CustomerId,

                Title =
                    "Chưa xác nhận được chuyển khoản",

                Message =
                    $"SmartCar chưa tìm thấy giao dịch " +
                    $"{GetPaymentLabel(payment.Type)} của đơn " +
                    $"#{payment.BookingId}. " +
                    $"Vui lòng kiểm tra và gửi lại sau khi đã chuyển khoản."
            });

        await _dbContext
            .SaveChangesAsync(
                cancellationToken);

        await transaction
            .CommitAsync(
                cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "RejectQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),

            $"Từ chối xác nhận QR " +
            $"{GetPaymentLabel(payment.Type)} " +
            $"của đơn #{payment.BookingId}.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult>
        ConfirmRefundAsync(
            int paymentId,
            string adminId,
            string? transactionCode,
            CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(
                transactionCode) &&
            transactionCode.Trim().Length > 100)
        {
            return OperationResult.Failure(
                "Mã giao dịch hoàn tiền tối đa 100 ký tự.");
        }

        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
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
                PaymentType.Refund ||
            payment.Status !=
                PaymentStatus.AwaitingRefund)
        {
            return OperationResult.Failure(
                "Khoản hoàn tiền không còn ở trạng thái chờ xử lý.");
        }

        payment.Status =
            PaymentStatus.Refunded;

        payment.Method =
            PaymentMethods.BankTransferRefund;

        payment.PaidAt =
            DateTime.UtcNow;

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
                    $"SmartCar đã hoàn " +
                    $"{payment.Amount:N0} đồng cho đơn " +
                    $"#{payment.BookingId}. " +
                    $"Mã giao dịch: {payment.TransactionCode}."
            });

        await _dbContext
            .SaveChangesAsync(
                cancellationToken);

        await transaction
            .CommitAsync(
                cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Refund",
            nameof(Payment),
            payment.PaymentId.ToString(),

            $"Hoàn {payment.Amount:N0} đồng cho đơn " +
            $"#{payment.BookingId}. " +
            $"Mã giao dịch: {payment.TransactionCode}.",

            cancellationToken:
                cancellationToken);

        return OperationResult.Success();
    }

    private static string?
        ValidateCustomerPaymentState(
            Booking booking,
            PaymentType type) =>
        type switch
        {
            PaymentType.Deposit =>
                "Tiền cọc được gộp trong một lần chuyển khoản với tiền thuê/phí giao.",

            PaymentType.Rental
                when booking.Status !=
                     BookingStatus.PendingPayment =>
                "Đơn không ở trạng thái chờ thanh toán trước khi nhận xe.",

            PaymentType.Extension
                when booking.Status !=
                         BookingStatus.Rented ||
                     !booking.Extensions.Any(
                         extension =>
                             extension.Status ==
                             BookingExtensionStatus.Approved) =>
                "Không có yêu cầu gia hạn đã duyệt đang chờ thanh toán.",

            PaymentType.AdditionalCharge
                when booking.Status !=
                         BookingStatus.PendingInspection ||
                     booking.AdditionalAmount <= 0 =>
                "Đơn không có phụ phí đang chờ thanh toán.",

            _ => null
        };

    private static string?
        ValidateAdminConfirmationState(
            Booking booking,
            PaymentType type) =>
        type switch
        {
            PaymentType.Deposit =>
                "Không xác nhận tiền cọc riêng. " +
                "Hãy xác nhận giao dịch tổng tiền thuê/phí giao + cọc.",

            PaymentType.Rental
                when booking.Status !=
                     BookingStatus.PendingPayment =>
                "Đơn không còn ở trạng thái chờ thanh toán trước khi nhận xe.",

            PaymentType.Extension
                when booking.Status !=
                         BookingStatus.Rented ||
                     !booking.Extensions.Any(
                         extension =>
                             extension.Status ==
                             BookingExtensionStatus.Approved) =>
                "Không còn yêu cầu gia hạn đã duyệt đang chờ thanh toán.",

            PaymentType.AdditionalCharge
                when booking.Status !=
                     BookingStatus.PendingInspection =>
                "Đơn không còn ở trạng thái chờ thanh toán phụ phí.",

            _ => null
        };

    private async Task NotifyAdminsAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var roleId =
            await _dbContext.Roles

                .Where(role =>
                    role.Name ==
                    RoleNames.Admin)

                .Select(role =>
                    role.Id)

                .FirstOrDefaultAsync(
                    cancellationToken);

        if (string.IsNullOrWhiteSpace(
                roleId))
        {
            return;
        }

        var adminIds =
            await _dbContext.UserRoles

                .Where(item =>
                    item.RoleId ==
                    roleId)

                .Select(item =>
                    item.UserId)

                .ToListAsync(
                    cancellationToken);

        foreach (var adminId in adminIds)
        {
            _dbContext.Notifications.Add(
                new Notification
                {
                    UserId =
                        adminId,

                    Title =
                        title,

                    Message =
                        message
                });
        }
    }

    private static string GetPaymentLabel(
        PaymentType type) =>
        type switch
        {
            PaymentType.Rental =>
                "tiền thuê",

            PaymentType.Deposit =>
                "tiền cọc",

            PaymentType.Extension =>
                "tiền gia hạn",

            PaymentType.AdditionalCharge =>
                "phụ phí",

            PaymentType.Refund =>
                "hoàn tiền",

            _ =>
                "thanh toán"
        };
}