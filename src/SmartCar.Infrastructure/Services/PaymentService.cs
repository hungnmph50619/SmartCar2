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
            .Where(payment => payment.Type != PaymentType.Deposit)
            .AsQueryable();

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
                payment.Type == PaymentType.Rental
                    ? payment.Amount + payment.Booking.Payments
                        .Where(deposit =>
                            deposit.Type == PaymentType.Deposit &&
                            (payment.TransactionCode != null
                                ? deposit.TransactionCode == payment.TransactionCode
                                : deposit.TransactionCode == null && deposit.Status == payment.Status))
                        .Sum(deposit => deposit.Amount)
                    : payment.Amount,
                payment.Method,
                payment.Status,
                payment.PaidAt,
                payment.TransactionCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult> SubmitQrPaymentAsync(
        int bookingId,
        string customerId,
        PaymentType paymentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return OperationResult.Failure("Không xác định được khách hàng.");
        }

        if (!Enum.IsDefined(paymentType) || paymentType is PaymentType.Refund or PaymentType.Deposit)
        {
            return OperationResult.Failure("Loại thanh toán không hợp lệ.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
                .ThenInclude(payment => payment.VehicleIncident)
            .Include(item => item.Extensions)
            .FirstOrDefaultAsync(
                item => item.BookingId == bookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        var stateError = ValidateCustomerPaymentState(booking, paymentType);
        if (stateError is not null)
        {
            return OperationResult.Failure(stateError);
        }

        var awaitingPayment = booking.Payments.FirstOrDefault(payment =>
            payment.Type == paymentType &&
            payment.Status == PaymentStatus.AwaitingConfirmation);

        if (awaitingPayment is not null)
        {
            return OperationResult.Failure(
                "Khoản này đã báo chuyển và đang chờ SmartCar xác nhận.");
        }

        var payment = booking.Payments.FirstOrDefault(item =>
            item.Type == paymentType &&
            item.Status == PaymentStatus.Pending);

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
                item.Type == paymentType &&
                item.Status == PaymentStatus.Paid);

            return alreadyPaid
                ? OperationResult.Failure("Khoản tiền này đã được thanh toán.")
                : OperationResult.Failure("Không tìm thấy khoản thanh toán phù hợp.");
        }

        if (payment.Amount <= 0)
        {
            return OperationResult.Failure("Số tiền thanh toán phải lớn hơn 0.");
        }

        if (paymentType == PaymentType.TrafficFine)
        {
            if (payment.VehicleIncidentId is null ||
                payment.VehicleIncident is null ||
                payment.VehicleIncident.BookingId != booking.BookingId ||
                payment.VehicleIncident.IncidentType != IncidentType.TrafficFine ||
                payment.VehicleIncident.Status == IncidentStatus.Cancelled)
            {
                return OperationResult.Failure(
                    "Khoản phạt/vi phạm không còn gắn với hồ sơ hợp lệ của chuyến thuê này.");
            }
        }

        if (paymentType == PaymentType.Rental &&
            booking.PickupMethod == VehiclePickupMethod.Delivery)
        {
            var storedDeliveryFee = Math.Max(
                0m,
                booking.TotalAmount - booking.RentalAmount - booking.AdditionalAmount);

            if (storedDeliveryFee <= 0m &&
                booking.DeliveryLatitude.HasValue &&
                booking.DeliveryLongitude.HasValue)
            {
                storedDeliveryFee = RentalPolicy.CalculateDeliveryFee(
                    booking.PickupMethod,
                    booking.DeliveryLatitude,
                    booking.DeliveryLongitude);
            }

            payment.Amount = booking.RentalAmount + storedDeliveryFee;
            booking.TotalAmount = booking.RentalAmount + storedDeliveryFee + booking.AdditionalAmount;
        }

        payment.Method = PaymentMethods.BankQr;
        payment.Status = PaymentStatus.AwaitingConfirmation;
        payment.PaidAt = null;
        payment.TransactionCode = null;

        Payment? bundledDeposit = null;

        if (paymentType == PaymentType.Rental && booking.DepositAmount > 0)
        {
            bundledDeposit = booking.Payments
                .FirstOrDefault(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status != PaymentStatus.Paid);

            if (bundledDeposit is null &&
                !booking.Payments.Any(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.Paid))
            {
                bundledDeposit = new Payment
                {
                    BookingId = booking.BookingId,
                    Type = PaymentType.Deposit,
                    Amount = booking.DepositAmount,
                    Method = PaymentMethods.BankQr,
                    Status = PaymentStatus.AwaitingConfirmation
                };
                booking.Payments.Add(bundledDeposit);
            }
            else if (bundledDeposit is not null)
            {
                bundledDeposit.Amount = booking.DepositAmount;
                bundledDeposit.Method = PaymentMethods.BankQr;
                bundledDeposit.Status = PaymentStatus.AwaitingConfirmation;
                bundledDeposit.PaidAt = null;
                bundledDeposit.TransactionCode = null;
            }
        }

        var submittedAmount = payment.Amount + (bundledDeposit?.Amount ?? 0m);

        await NotifyAdminsAsync(
            "Có giao dịch chờ xác nhận",
            paymentType == PaymentType.Rental
                ? $"Đơn #{booking.BookingId} báo đã chuyển {submittedAmount:N0} đồng gồm tiền thuê/phí giao và cọc."
                : $"Đơn #{booking.BookingId} báo đã chuyển {submittedAmount:N0} đồng cho {GetPaymentLabel(payment.Type)}.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            customerId,
            "SubmitQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Khách báo đã chuyển {submittedAmount:N0} đồng cho {GetPaymentLabel(paymentType)} của đơn #{booking.BookingId}.",
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
            .Include(item => item.VehicleIncident)
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Extensions)
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

        if (payment.Amount <= 0)
        {
            return OperationResult.Failure("Không thể xác nhận giao dịch có số tiền không hợp lệ.");
        }

        var booking = payment.Booking;
        var originalType = payment.Type;
        var confirmedAmount = payment.Amount;

        var stateError = ValidateAdminConfirmationState(booking, originalType);
        if (stateError is not null)
        {
            return OperationResult.Failure(stateError);
        }

        if (originalType == PaymentType.TrafficFine &&
            (payment.VehicleIncidentId is null ||
             payment.VehicleIncident is null ||
             payment.VehicleIncident.BookingId != booking.BookingId ||
             payment.VehicleIncident.IncidentType != IncidentType.TrafficFine ||
             payment.VehicleIncident.Status == IncidentStatus.Cancelled))
        {
            return OperationResult.Failure(
                "Không thể xác nhận vì khoản phạt không còn gắn với hồ sơ vi phạm hợp lệ.");
        }

        var paidAt = DateTime.UtcNow;
        var transactionCode = $"QR{paidAt:yyyyMMddHHmmssfff}{booking.BookingId}";

        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = paidAt;
        payment.TransactionCode = transactionCode;

        Payment? bundledDeposit = null;

        if (originalType == PaymentType.Rental)
        {
            bundledDeposit = booking.Payments
                .FirstOrDefault(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.AwaitingConfirmation);

            if (booking.DepositAmount > 0 && bundledDeposit is not null)
            {
                bundledDeposit.Amount = booking.DepositAmount;
                bundledDeposit.Method = PaymentMethods.BankQr;
                bundledDeposit.Status = PaymentStatus.Paid;
                bundledDeposit.PaidAt = paidAt;
                bundledDeposit.TransactionCode = transactionCode;
            }

            booking.Status = BookingStatus.Paid;
            booking.ReservationExpiresAt = null;
        }
        else if (originalType == PaymentType.Extension)
        {
            var extension = booking.Extensions
                .Where(item => item.Status == BookingExtensionStatus.Approved)
                .OrderByDescending(item => item.RequestedAt)
                .FirstOrDefault();

            if (extension is null)
            {
                return OperationResult.Failure("Không còn yêu cầu gia hạn chờ thanh toán.");
            }

            if (extension.AdditionalAmount != confirmedAmount)
            {
                return OperationResult.Failure(
                    $"Số tiền giao dịch ({confirmedAmount:N0} đồng) không khớp khoản gia hạn đã duyệt ({extension.AdditionalAmount:N0} đồng). " +
                    "Không xác nhận để tránh sai lệch quyết toán.");
            }

            if (extension.RequestedReturnDate <= booking.ReturnDate)
            {
                return OperationResult.Failure(
                    "Ngày trả của gia hạn không còn lớn hơn ngày trả hiện tại. Vui lòng kiểm tra lại dữ liệu gia hạn.");
            }

            // Cập nhật hợp đồng trong CÙNG transaction với xác nhận tiền.
            // Như vậy không thể có trạng thái payment=Paid nhưng ngày trả chưa được gia hạn.
            booking.ReturnDate = extension.RequestedReturnDate;
            booking.NumberOfDays = Math.Max(
                1,
                (int)Math.Ceiling((booking.ReturnDate - booking.PickupDate).TotalHours / 24d));
            booking.RentalAmount += extension.AdditionalAmount;
            booking.TotalAmount += extension.AdditionalAmount;
            extension.Status = BookingExtensionStatus.Paid;
            extension.PaidAt = paidAt;
        }
        else if (originalType == PaymentType.VehicleSwapAdjustment)
        {
            var currentDepositPaid = booking.Payments
                .Where(item =>
                    item.PaymentId != payment.PaymentId &&
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.Paid)
                .Sum(item => item.Amount);

            var depositTopUp = Math.Min(
                confirmedAmount,
                Math.Max(0m, booking.DepositAmount - currentDepositPaid));
            var rentalAdjustment = Math.Max(0m, confirmedAmount - depositTopUp);

            if (depositTopUp > 0 && rentalAdjustment > 0)
            {
                payment.Amount = rentalAdjustment;
                booking.Payments.Add(new Payment
                {
                    BookingId = booking.BookingId,
                    Type = PaymentType.Deposit,
                    Amount = depositTopUp,
                    Method = PaymentMethods.BankQr,
                    Status = PaymentStatus.Paid,
                    PaidAt = paidAt,
                    TransactionCode = transactionCode
                });
            }
            else if (depositTopUp > 0)
            {
                payment.Type = PaymentType.Deposit;
                payment.Amount = depositTopUp;
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Thanh toán đã được xác nhận",
            Message = originalType switch
            {
                PaymentType.Rental =>
                    $"Đơn #{booking.BookingId}: đã xác nhận {(payment.Amount + (bundledDeposit?.Amount ?? 0m)):N0} đồng tiền thuê/phí giao và cọc.",
                PaymentType.Extension =>
                    $"Đơn #{booking.BookingId}: đã xác nhận tiền gia hạn {confirmedAmount:N0} đồng; ngày trả mới {booking.ReturnDate:dd/MM/yyyy HH:mm}.",
                PaymentType.VehicleSwapAdjustment =>
                    $"Đơn #{booking.BookingId}: đã xác nhận chênh lệch đổi xe {confirmedAmount:N0} đồng.",
                PaymentType.AdditionalCharge =>
                    $"Đơn #{booking.BookingId}: đã xác nhận phụ phí {confirmedAmount:N0} đồng.",
                PaymentType.TrafficFine =>
                    $"Đơn #{booking.BookingId}: đã xác nhận thanh toán nghĩa vụ phạt/vi phạm {confirmedAmount:N0} đồng.",
                _ =>
                    $"Đơn #{booking.BookingId}: đã xác nhận {confirmedAmount:N0} đồng."
            }
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ConfirmQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Xác nhận {GetPaymentLabel(originalType)} {confirmedAmount:N0} đồng cho đơn #{booking.BookingId}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RejectQrPaymentAsync(
        int paymentId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
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

        payment.Status = PaymentStatus.Pending;
        payment.Method = PaymentMethods.NotSelected;
        payment.PaidAt = null;
        payment.TransactionCode = null;

        if (payment.Type == PaymentType.Rental)
        {
            var bundledDeposit = payment.Booking.Payments
                .FirstOrDefault(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.AwaitingConfirmation);

            if (bundledDeposit is not null)
            {
                bundledDeposit.Status = PaymentStatus.Pending;
                bundledDeposit.Method = PaymentMethods.NotSelected;
                bundledDeposit.PaidAt = null;
                bundledDeposit.TransactionCode = null;
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "Chưa xác nhận được chuyển khoản",
            Message =
                $"SmartCar chưa xác nhận được {GetPaymentLabel(payment.Type)} của đơn #{payment.BookingId}. " +
                "Vui lòng kiểm tra và gửi lại xác nhận."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "RejectQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Từ chối xác nhận {GetPaymentLabel(payment.Type)} của đơn #{payment.BookingId}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmRefundAsync(
        int paymentId,
        string adminId,
        string? transactionCode,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(transactionCode) && transactionCode.Trim().Length > 100)
        {
            return OperationResult.Failure("Mã giao dịch hoàn tiền tối đa 100 ký tự.");
        }

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

        if (payment.Type != PaymentType.Refund ||
            payment.Status != PaymentStatus.AwaitingRefund)
        {
            return OperationResult.Failure("Khoản hoàn tiền không còn ở trạng thái chờ xử lý.");
        }

        var refundPurpose = payment.Method;

        payment.Status = PaymentStatus.Refunded;
        if (refundPurpose is not (
            PaymentMethods.DepositRefund or
            PaymentMethods.VehicleSwapRefund or
            PaymentMethods.CompensationRefund))
        {
            payment.Method = PaymentMethods.BankTransferRefund;
        }

        payment.PaidAt = DateTime.UtcNow;
        payment.TransactionCode = string.IsNullOrWhiteSpace(transactionCode)
            ? $"RF{DateTime.UtcNow:yyyyMMddHHmmssfff}{payment.BookingId}"
            : transactionCode.Trim();

        var purposeText = payment.Method switch
        {
            PaymentMethods.DepositRefund => "hoàn cọc",
            PaymentMethods.VehicleSwapRefund => "hoàn chênh lệch đổi xe",
            PaymentMethods.CompensationRefund => "hỗ trợ/bồi thường",
            _ => "hoàn tiền"
        };

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "Hoàn tiền thành công",
            Message =
                $"Đơn #{payment.BookingId}: SmartCar đã {purposeText} {payment.Amount:N0} đồng. " +
                $"Mã giao dịch: {payment.TransactionCode}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "Refund",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"{purposeText} {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. Mã giao dịch: {payment.TransactionCode}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    private static string? ValidateCustomerPaymentState(
        Booking booking,
        PaymentType type) =>
        type switch
        {
            PaymentType.Deposit =>
                "Tiền cọc được gộp trong khoản thanh toán hệ thống yêu cầu.",

            PaymentType.Rental
                when booking.Status != BookingStatus.PendingPayment =>
                "Đơn không ở trạng thái chờ thanh toán trước khi nhận xe.",

            PaymentType.Extension
                when booking.Status != BookingStatus.Rented ||
                     !booking.Extensions.Any(extension => extension.Status == BookingExtensionStatus.Approved) =>
                "Không có yêu cầu gia hạn đã duyệt đang chờ thanh toán.",

            PaymentType.VehicleSwapAdjustment
                when booking.Status is not (
                    BookingStatus.PendingPayment or
                    BookingStatus.Paid or
                    BookingStatus.ReadyForPickup) =>
                "Đơn không còn ở trạng thái thanh toán chênh lệch đổi xe.",

            PaymentType.AdditionalCharge
                when booking.Status != BookingStatus.PendingInspection ||
                     booking.AdditionalAmount <= 0 =>
                "Đơn không có phụ phí đang chờ thanh toán.",

            PaymentType.TrafficFine
                when !booking.Payments.Any(payment =>
                    payment.Type == PaymentType.TrafficFine &&
                    payment.Status == PaymentStatus.Pending &&
                    payment.Amount > 0) =>
                "Không có nghĩa vụ phạt/vi phạm hợp lệ đang chờ thanh toán.",

            _ => null
        };

    private static string? ValidateAdminConfirmationState(
        Booking booking,
        PaymentType type) =>
        type switch
        {
            PaymentType.Deposit =>
                "Không xác nhận tiền cọc riêng.",

            PaymentType.Rental
                when booking.Status != BookingStatus.PendingPayment =>
                "Đơn không còn ở trạng thái chờ thanh toán trước khi nhận xe.",

            PaymentType.Extension
                when booking.Status != BookingStatus.Rented ||
                     !booking.Extensions.Any(extension => extension.Status == BookingExtensionStatus.Approved) =>
                "Không còn yêu cầu gia hạn đã duyệt đang chờ thanh toán.",

            PaymentType.VehicleSwapAdjustment
                when booking.Status is not (
                    BookingStatus.PendingPayment or
                    BookingStatus.Paid or
                    BookingStatus.ReadyForPickup) =>
                "Đơn không còn ở trạng thái đối soát chênh lệch đổi xe.",

            PaymentType.AdditionalCharge
                when booking.Status != BookingStatus.PendingInspection =>
                "Đơn không còn ở trạng thái chờ thanh toán phụ phí.",

            PaymentType.TrafficFine
                when !booking.Payments.Any(payment =>
                    payment.Type == PaymentType.TrafficFine &&
                    payment.Status == PaymentStatus.AwaitingConfirmation &&
                    payment.Amount > 0) =>
                "Không còn nghĩa vụ phạt/vi phạm chờ đối soát.",

            _ => null
        };

    private async Task NotifyAdminsAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var roleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(roleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .Where(item => item.RoleId == roleId)
            .Select(item => item.UserId)
            .ToListAsync(cancellationToken);

        foreach (var adminId in adminIds)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            });
        }
    }

    private static string GetPaymentLabel(PaymentType type) => type switch
    {
        PaymentType.Rental => "tiền thuê",
        PaymentType.Deposit => "tiền cọc",
        PaymentType.Extension => "tiền gia hạn",
        PaymentType.VehicleSwapAdjustment => "chênh lệch đổi xe",
        PaymentType.AdditionalCharge => "phụ phí",
        PaymentType.TrafficFine => "nghĩa vụ phạt/vi phạm",
        PaymentType.Refund => "hoàn tiền",
        _ => "thanh toán"
    };
}
