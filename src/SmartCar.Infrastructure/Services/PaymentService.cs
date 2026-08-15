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

        if (paymentType is PaymentType.Refund or PaymentType.DepositRefund or PaymentType.Deposit)
        {
            return OperationResult.Failure(
                "Cọc bảo đảm được thanh toán chung với tiền thuê; không thể thanh toán riêng khoản cọc hoặc khoản hoàn.");
        }

        if (paymentType == PaymentType.Rental && booking.Status != BookingStatus.PendingPayment)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán tiền thuê và cọc.");
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
            var depositAmount = GetPaidDepositAmount(booking);
            var amountDueAfterDeposit = Math.Max(0, booking.AdditionalAmount - depositAmount);
            if (amountDueAfterDeposit <= 0)
            {
                return OperationResult.Failure(
                    "Toàn bộ phụ phí hiện tại đã được cọc bảo đảm bao phủ. Vui lòng chờ SmartCar hoàn tất kiểm tra và quyết toán cọc.");
            }

            payment = new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.AdditionalCharge,
                Amount = amountDueAfterDeposit,
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

        var paidAt = DateTime.UtcNow;
        var transactionCode = $"SC{paidAt:yyyyMMddHHmmssfff}{booking.BookingId}";
        payment.Method = PaymentMethods.Simulation;
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = paidAt;
        payment.TransactionCode = transactionCode;

        decimal auditAmount = payment.Amount;

        if (paymentType == PaymentType.Rental)
        {
            var depositPayment = EnsureDepositPayment(booking);
            if (depositPayment.Status == PaymentStatus.Paid)
            {
                return OperationResult.Failure("Cọc bảo đảm của đơn này đã được thanh toán.");
            }

            depositPayment.Amount = RentalPolicyConstants.SecurityDepositAmount;
            depositPayment.Method = PaymentMethods.Simulation;
            depositPayment.Status = PaymentStatus.Paid;
            depositPayment.PaidAt = paidAt;
            depositPayment.TransactionCode = transactionCode;

            auditAmount += depositPayment.Amount;
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
            extension.PaidAt = paidAt;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Thanh toán thành công",
            Message = paymentType switch
            {
                PaymentType.Rental =>
                    $"Đơn #{booking.BookingId} đã thanh toán một lần {auditAmount:N0} đồng, gồm tiền thuê/phí giao nhận {payment.Amount:N0} đồng và cọc bảo đảm {RentalPolicyConstants.SecurityDepositAmount:N0} đồng.",
                PaymentType.Extension => $"Đơn #{booking.BookingId} đã thanh toán tiền gia hạn thành công.",
                PaymentType.AdditionalCharge => $"Đơn #{booking.BookingId} đã thanh toán phần phụ phí vượt cọc thành công.",
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
            paymentType == PaymentType.Rental
                ? $"Thanh toán mô phỏng một lần cho đơn #{booking.BookingId}: tổng {auditAmount:N0} đồng, gồm tiền thuê/phí giao nhận {payment.Amount:N0} đồng và cọc {RentalPolicyConstants.SecurityDepositAmount:N0} đồng; mã giao dịch {transactionCode}."
                : $"Thanh toán mô phỏng {paymentType} cho đơn #{booking.BookingId}, số tiền {payment.Amount:N0} đồng, mã giao dịch {transactionCode}.",
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

        if (paymentType is PaymentType.Refund or PaymentType.DepositRefund or PaymentType.Deposit)
        {
            return OperationResult.Failure(
                "Cọc bảo đảm được thanh toán chung với tiền thuê; không thể gửi riêng khoản cọc hoặc khoản hoàn.");
        }

        if (paymentType == PaymentType.Rental && booking.Status != BookingStatus.PendingPayment)
        {
            return OperationResult.Failure("Đơn không ở trạng thái chờ thanh toán tiền thuê và cọc.");
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
            var depositAmount = GetPaidDepositAmount(booking);
            var amountDueAfterDeposit = Math.Max(0, booking.AdditionalAmount - depositAmount);
            if (amountDueAfterDeposit <= 0)
            {
                return OperationResult.Failure(
                    "Toàn bộ phụ phí hiện tại đã được cọc bảo đảm bao phủ. Vui lòng chờ SmartCar hoàn tất kiểm tra và quyết toán cọc.");
            }

            payment = new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.AdditionalCharge,
                Amount = amountDueAfterDeposit,
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
        var reconciliationCode = $"QRREQ{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";
        payment.Method = PaymentMethods.BankQr;
        payment.Status = PaymentStatus.AwaitingConfirmation;
        payment.PaidAt = null;
        payment.TransactionCode = reconciliationCode;

        decimal submittedAmount = payment.Amount;
        if (paymentType == PaymentType.Rental)
        {
            var depositPayment = EnsureDepositPayment(booking);
            if (depositPayment.Status == PaymentStatus.Paid)
            {
                return OperationResult.Failure("Cọc bảo đảm của đơn này đã được thanh toán.");
            }

            depositPayment.Amount = RentalPolicyConstants.SecurityDepositAmount;
            depositPayment.Method = PaymentMethods.BankQr;
            depositPayment.Status = PaymentStatus.AwaitingConfirmation;
            depositPayment.PaidAt = null;
            depositPayment.TransactionCode = reconciliationCode;
            submittedAmount += depositPayment.Amount;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã gửi xác nhận chuyển khoản",
            Message = paymentType switch
            {
                PaymentType.Rental =>
                    $"Khoản thanh toán một lần {submittedAmount:N0} đồng của đơn #{booking.BookingId}, gồm tiền thuê/phí giao nhận và cọc bảo đảm, đang chờ SmartCar xác nhận.",
                PaymentType.Extension => $"Tiền gia hạn của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",
                PaymentType.AdditionalCharge => $"Phần phụ phí vượt cọc của đơn #{booking.BookingId} đang chờ SmartCar xác nhận.",
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
            paymentType == PaymentType.Rental
                ? $"Khách báo đã chuyển khoản QR một lần cho đơn #{booking.BookingId}, tổng {submittedAmount:N0} đồng gồm tiền thuê/phí giao nhận {payment.Amount:N0} đồng và cọc {RentalPolicyConstants.SecurityDepositAmount:N0} đồng, mã yêu cầu đối soát {reconciliationCode}." +
                  (string.IsNullOrWhiteSpace(previousReconciliationCode)
                      ? string.Empty
                      : $" Mã yêu cầu trước đó: {previousReconciliationCode}.")
                : $"Khách báo đã chuyển khoản QR {paymentType} cho đơn #{booking.BookingId}, số tiền {payment.Amount:N0} đồng, mã yêu cầu đối soát {reconciliationCode}." +
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

        if (payment.Type is PaymentType.Refund or PaymentType.DepositRefund)
        {
            return OperationResult.Failure("Khoản hoàn không được xác nhận bằng luồng thanh toán QR của khách.");
        }

        var booking = payment.Booking;
        var isInitialCheckout = payment.Type is PaymentType.Rental or PaymentType.Deposit;

        if (isInitialCheckout)
        {
            if (booking.Status is not (BookingStatus.PendingPayment or BookingStatus.Cancelled))
            {
                return OperationResult.Failure("Đơn không còn ở trạng thái có thể xác nhận khoản thanh toán ban đầu.");
            }

            var rentalPayment = booking.Payments.FirstOrDefault(item =>
                item.Type == PaymentType.Rental &&
                item.Status == PaymentStatus.AwaitingConfirmation &&
                item.Method == PaymentMethods.BankQr);
            var depositPayment = booking.Payments.FirstOrDefault(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.AwaitingConfirmation &&
                item.Method == PaymentMethods.BankQr);

            if (rentalPayment is null || depositPayment is null)
            {
                return OperationResult.Failure(
                    "Giao dịch thanh toán ban đầu chưa đủ hai cấu phần tiền thuê và cọc bảo đảm để đối soát.");
            }

            if (!string.Equals(
                    rentalPayment.TransactionCode,
                    depositPayment.TransactionCode,
                    StringComparison.Ordinal))
            {
                return OperationResult.Failure(
                    "Mã đối soát tiền thuê và cọc không khớp. Vui lòng kiểm tra trước khi xác nhận.");
            }

            var confirmedAt = DateTime.UtcNow;
            var bankTransactionCode = $"QR{confirmedAt:yyyyMMddHHmmssfff}{booking.BookingId}";
            rentalPayment.Status = PaymentStatus.Paid;
            rentalPayment.PaidAt = confirmedAt;
            rentalPayment.TransactionCode = bankTransactionCode;
            depositPayment.Status = PaymentStatus.Paid;
            depositPayment.PaidAt = confirmedAt;
            depositPayment.TransactionCode = bankTransactionCode;

            var checkoutAmount = rentalPayment.Amount + depositPayment.Amount;
            var confirmingCancelledCheckout = booking.Status == BookingStatus.Cancelled;

            if (!confirmingCancelledCheckout)
            {
                booking.Status = BookingStatus.Paid;
                _dbContext.Notifications.Add(new Notification
                {
                    UserId = booking.CustomerId,
                    Title = "Thanh toán đã được xác nhận",
                    Message =
                        $"SmartCar đã xác nhận giao dịch {checkoutAmount:N0} đồng của đơn #{booking.BookingId}: " +
                        $"tiền thuê/phí giao nhận {rentalPayment.Amount:N0} đồng và cọc bảo đảm {depositPayment.Amount:N0} đồng. " +
                        "Bạn không cần thanh toán cọc lần nữa khi nhận xe."
                });
            }
            else
            {
                var cancellationTime = booking.CancelledAt?.ToLocalTime() ?? DateTime.Now;
                var hoursBeforePickup = (booking.PickupDate - cancellationTime).TotalHours;

                decimal rentalRefundAmount;
                string rentalRefundReason;

                if (string.Equals(booking.CancelledBy, "Quản trị viên", StringComparison.OrdinalIgnoreCase))
                {
                    rentalRefundAmount = rentalPayment.Amount;
                    rentalRefundReason = "SmartCar chủ động hủy trước khi giao xe: hoàn 100% phần tiền thuê/phí giao nhận.";
                }
                else if (hoursBeforePickup >= 48)
                {
                    rentalRefundAmount = rentalPayment.Amount;
                    rentalRefundReason = "Khách hủy trước thời gian nhận xe từ 48 giờ trở lên: hoàn 100% phần tiền thuê/phí giao nhận.";
                }
                else if (hoursBeforePickup >= 24)
                {
                    rentalRefundAmount = Math.Round(rentalPayment.Amount * 0.5m, 0);
                    rentalRefundReason = "Khách hủy trước thời gian nhận xe từ 24 đến dưới 48 giờ: hoàn 50% phần tiền thuê/phí giao nhận.";
                }
                else
                {
                    rentalRefundAmount = 0;
                    rentalRefundReason = "Khách hủy trước thời gian nhận xe dưới 24 giờ: không hoàn phần tiền thuê/phí giao nhận.";
                }

                var refundAmount = rentalRefundAmount + depositPayment.Amount;
                booking.RefundAmount = refundAmount;
                booking.RefundReason =
                    $"{rentalRefundReason} Cọc bảo đảm {depositPayment.Amount:N0} đồng được hoàn 100% vì xe chưa được bàn giao. Tổng hoàn {refundAmount:N0} đồng.";

                var existingRefund = booking.Payments.FirstOrDefault(item => item.Type == PaymentType.Refund);
                if (refundAmount > 0)
                {
                    if (existingRefund is null)
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
                    else if (existingRefund.Status == PaymentStatus.AwaitingRefund)
                    {
                        existingRefund.Amount = refundAmount;
                    }
                }

                _dbContext.Notifications.Add(new Notification
                {
                    UserId = booking.CustomerId,
                    Title = refundAmount > 0
                        ? "Khoản hoàn tiền đã được tạo"
                        : "Đã xác nhận khoản chuyển của đơn đã hủy",
                    Message = refundAmount > 0
                        ? $"SmartCar đã xác nhận giao dịch {checkoutAmount:N0} đồng của đơn #{booking.BookingId}. " +
                          $"Theo chính sách hủy, tổng khoản hoàn {refundAmount:N0} đồng đã được tạo, trong đó cọc {depositPayment.Amount:N0} đồng được hoàn 100%."
                        : $"SmartCar đã xác nhận giao dịch {checkoutAmount:N0} đồng của đơn #{booking.BookingId}. {booking.RefundReason}"
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await _auditService.WriteAsync(
                adminId,
                confirmingCancelledCheckout ? "ConfirmCancelledBookingPayment" : "ConfirmQrPayment",
                nameof(Payment),
                rentalPayment.PaymentId.ToString(),
                confirmingCancelledCheckout
                    ? $"Xác nhận giao dịch QR tiền thuê + cọc cho đơn đã hủy #{booking.BookingId}, tổng {checkoutAmount:N0} đồng; hoàn dự kiến {booking.RefundAmount:N0} đồng."
                    : $"Xác nhận giao dịch QR một lần cho đơn #{booking.BookingId}, tổng {checkoutAmount:N0} đồng gồm tiền thuê/phí giao nhận {rentalPayment.Amount:N0} đồng và cọc {depositPayment.Amount:N0} đồng.",
                cancellationToken: cancellationToken);

            return OperationResult.Success();
        }

        if (payment.Type == PaymentType.Extension)
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
        else
        {
            return OperationResult.Failure("Loại giao dịch không hợp lệ cho bước xác nhận QR.");
        }

        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = $"QR{DateTime.UtcNow:yyyyMMddHHmmssfff}{booking.BookingId}";

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Thanh toán đã được xác nhận",
            Message = payment.Type switch
            {
                PaymentType.Extension => $"SmartCar đã xác nhận tiền gia hạn {payment.Amount:N0} đồng của đơn #{booking.BookingId}.",
                PaymentType.AdditionalCharge => $"SmartCar đã xác nhận phần phụ phí vượt cọc {payment.Amount:N0} đồng của đơn #{booking.BookingId}.",
                _ => $"SmartCar đã xác nhận khoản thanh toán {payment.Amount:N0} đồng của đơn #{booking.BookingId}."
            }
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ConfirmQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Xác nhận chuyển khoản QR {payment.Type} cho đơn #{booking.BookingId}, {payment.Amount:N0} đồng.",
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

        var reconciliationCode = payment.TransactionCode;
        var isInitialCheckout = payment.Type is PaymentType.Rental or PaymentType.Deposit;
        decimal rejectedAmount = payment.Amount;

        if (isInitialCheckout)
        {
            var checkoutPayments = payment.Booking.Payments
                .Where(item =>
                    item.Type is PaymentType.Rental or PaymentType.Deposit &&
                    item.Status == PaymentStatus.AwaitingConfirmation &&
                    item.Method == PaymentMethods.BankQr)
                .ToList();

            if (checkoutPayments.Count != 2)
            {
                return OperationResult.Failure(
                    "Không tìm thấy đầy đủ tiền thuê và cọc của cùng giao dịch để trả về trạng thái chờ thanh toán.");
            }

            rejectedAmount = checkoutPayments.Sum(item => item.Amount);
            foreach (var checkoutPayment in checkoutPayments)
            {
                checkoutPayment.Status = PaymentStatus.Pending;
                checkoutPayment.Method = PaymentMethods.NotSelected;
                checkoutPayment.PaidAt = null;
                // Giữ mã QRREQ để phục vụ truy vết lần khách đã báo chuyển khoản.
            }
        }
        else
        {
            payment.Status = PaymentStatus.Pending;
            payment.Method = PaymentMethods.NotSelected;
            payment.PaidAt = null;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "SmartCar chưa tìm thấy giao dịch chuyển khoản",
            Message = isInitialCheckout
                ? $"SmartCar chưa đối soát được giao dịch thanh toán ban đầu {rejectedAmount:N0} đồng của đơn #{payment.BookingId}. " +
                  $"Mã yêu cầu: {reconciliationCode ?? "không có"}. Lý do đối soát: {normalizedReason}. " +
                  "Cả tiền thuê và cọc đã được trả về trạng thái chờ thanh toán; lịch sử đối soát vẫn được lưu."
                : $"SmartCar chưa đối soát được khoản {payment.Amount:N0} đồng của đơn #{payment.BookingId}. " +
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
            isInitialCheckout
                ? $"Admin chưa tìm thấy giao dịch QR một lần của đơn #{payment.BookingId}, tổng {rejectedAmount:N0} đồng gồm tiền thuê và cọc. Mã yêu cầu đối soát: {reconciliationCode ?? "không có"}. Lý do: {normalizedReason}."
                : $"Admin chưa tìm thấy chuyển khoản QR của đơn #{payment.BookingId}, số tiền {payment.Amount:N0} đồng. Mã yêu cầu đối soát: {reconciliationCode ?? "không có"}. Lý do: {normalizedReason}.",
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
            return OperationResult.Failure("Không tìm thấy khoản hoàn tiền/hoàn cọc.");
        }

        if (payment.Type is not (PaymentType.Refund or PaymentType.DepositRefund))
        {
            return OperationResult.Failure("Giao dịch này không phải khoản hoàn tiền hoặc hoàn cọc.");
        }

        if (payment.Status != PaymentStatus.AwaitingRefund)
        {
            return OperationResult.Failure("Khoản hoàn không còn ở trạng thái chờ xử lý.");
        }

        var isDepositRefund = payment.Type == PaymentType.DepositRefund;

        payment.Status = PaymentStatus.Refunded;
        payment.Method = isDepositRefund
            ? RentalPolicyConstants.SecurityDepositRefundMethod
            : PaymentMethods.BankTransferRefund;
        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = string.IsNullOrWhiteSpace(transactionCode)
            ? $"{(isDepositRefund ? "DRF" : "RF")}{DateTime.UtcNow:yyyyMMddHHmmssfff}{payment.BookingId}"
            : transactionCode.Trim();

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = isDepositRefund ? "Hoàn cọc thành công" : "Hoàn tiền thành công",
            Message = isDepositRefund
                ? $"SmartCar đã hoàn cọc {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. Mã giao dịch: {payment.TransactionCode}."
                : $"SmartCar đã hoàn {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. Mã giao dịch: {payment.TransactionCode}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            isDepositRefund ? "DepositRefund" : "Refund",
            nameof(Payment),
            payment.PaymentId.ToString(),
            isDepositRefund
                ? $"Hoàn cọc {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. Mã giao dịch: {payment.TransactionCode}."
                : $"Hoàn {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. Mã giao dịch: {payment.TransactionCode}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    private static Payment EnsureDepositPayment(Booking booking)
    {
        var depositPayment = booking.Payments.FirstOrDefault(payment =>
            payment.Type == PaymentType.Deposit);

        if (depositPayment is not null)
        {
            return depositPayment;
        }

        depositPayment = new Payment
        {
            BookingId = booking.BookingId,
            Type = PaymentType.Deposit,
            Amount = RentalPolicyConstants.SecurityDepositAmount,
            Method = PaymentMethods.NotSelected,
            Status = PaymentStatus.Pending
        };
        booking.Payments.Add(depositPayment);
        return depositPayment;
    }

    private static decimal GetPaidDepositAmount(Booking booking) =>
        booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);
}
