using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Application.Features.Operations;
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
        string actorId,
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

        if (paymentType == PaymentType.Rental &&
            booking.Payments.Any(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.AwaitingConfirmation))
        {
            return OperationResult.Failure(
                "Tiền cọc của đơn đang chờ SmartCar đối soát. Không tạo thêm yêu cầu chuyển khoản để tránh thu trùng.");
        }

        var payment = booking.Payments.FirstOrDefault(item =>
            item.Type == paymentType &&
            item.Status == PaymentStatus.Pending);

        if (payment is null && paymentType == PaymentType.Rental)
        {
            var requiredRentalAmount = GetRequiredRentalPaymentAmount(booking);
            var rentalPaidBefore = booking.Payments
                .Where(item =>
                    item.Status == PaymentStatus.Paid &&
                    item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
                .Sum(item => item.Amount);
            var outstandingRental = BookingWorkflowRules.CalculateOutstandingRental(
                requiredRentalAmount,
                rentalPaidBefore);

            if (outstandingRental > 0m)
            {
                payment = new Payment
                {
                    BookingId = booking.BookingId,
                    Type = PaymentType.Rental,
                    Amount = outstandingRental,
                    Method = PaymentMethods.NotSelected,
                    Status = PaymentStatus.Pending
                };
                booking.Payments.Add(payment);
            }
        }

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

        if (paymentType == PaymentType.Rental)
        {
            var requiredRentalAmount = GetRequiredRentalPaymentAmount(booking);
            var storedDeliveryFee = Math.Max(
                0m,
                requiredRentalAmount - booking.RentalAmount);
            var rentalPaidBefore = booking.Payments
                .Where(item =>
                    item.Status == PaymentStatus.Paid &&
                    item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
                .Sum(item => item.Amount);
            var outstandingRental = BookingWorkflowRules.CalculateOutstandingRental(
                requiredRentalAmount,
                rentalPaidBefore);

            if (outstandingRental <= 0m)
            {
                return OperationResult.Failure(
                    "Tiền thuê/phí giao của đơn đã được thanh toán đủ.");
            }

            payment.Amount = outstandingRental;
            booking.TotalAmount =
                requiredRentalAmount + booking.AdditionalAmount;

            foreach (var staleRental in booking.Payments.Where(item =>
                         item != payment &&
                         item.Type == PaymentType.Rental &&
                         item.Status == PaymentStatus.Pending))
            {
                staleRental.Status = PaymentStatus.Failed;
                staleRental.Method = PaymentMethods.NotSelected;
                staleRental.PaidAt = null;
                staleRental.TransactionCode = null;
            }
        }

        if (payment.Amount <= 0)
        {
            return OperationResult.Failure("Số tiền thanh toán phải lớn hơn 0.");
        }

        payment.Method = PaymentMethods.BankQr;
        payment.Status = PaymentStatus.AwaitingConfirmation;
        payment.PaidAt = null;
        payment.TransactionCode = null;

        Payment? bundledDeposit = null;

        if (paymentType == PaymentType.Rental && booking.DepositAmount > 0)
        {
            var depositPaid = booking.Payments
                .Where(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.Paid)
                .Sum(item => item.Amount);

            var outstandingDeposit = BookingWorkflowRules.CalculateOutstandingDeposit(
                booking.DepositAmount,
                depositPaid);

            if (outstandingDeposit > 0m)
            {
                bundledDeposit = booking.Payments
                    .FirstOrDefault(item =>
                        item.Type == PaymentType.Deposit &&
                        item.Status == PaymentStatus.Pending);

                if (bundledDeposit is null)
                {
                    bundledDeposit = new Payment
                    {
                        BookingId = booking.BookingId,
                        Type = PaymentType.Deposit,
                        Amount = outstandingDeposit,
                        Method = PaymentMethods.BankQr,
                        Status = PaymentStatus.AwaitingConfirmation
                    };
                    booking.Payments.Add(bundledDeposit);
                }
                else
                {
                    bundledDeposit.Amount = outstandingDeposit;
                    bundledDeposit.Method = PaymentMethods.BankQr;
                    bundledDeposit.Status = PaymentStatus.AwaitingConfirmation;
                    bundledDeposit.PaidAt = null;
                    bundledDeposit.TransactionCode = null;
                }
            }

            foreach (var staleDeposit in booking.Payments.Where(item =>
                         item != bundledDeposit &&
                         item.Type == PaymentType.Deposit &&
                         item.Status == PaymentStatus.Pending))
            {
                staleDeposit.Status = PaymentStatus.Failed;
                staleDeposit.Method = PaymentMethods.NotSelected;
                staleDeposit.PaidAt = null;
                staleDeposit.TransactionCode = null;
            }
        }

        var submittedAmount = payment.Amount + (bundledDeposit?.Amount ?? 0m);

        if (booking.Status == BookingStatus.PendingPayment &&
            paymentType is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
        {
            booking.ReservationExpiresAt = DateTime.UtcNow
                .AddMinutes(RentalPolicy.BookingTransferReconciliationHoldMinutes);
        }

        await NotifyStaffAsync(
            "Có giao dịch chờ đối soát",
            paymentType == PaymentType.Rental
                ? $"Đơn #{booking.BookingId} báo đã chuyển {submittedAmount:N0} đồng gồm tiền thuê/phí giao và cọc."
                : $"Đơn #{booking.BookingId} báo đã chuyển {submittedAmount:N0} đồng cho {GetPaymentLabel(payment.Type)}.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            actorId,
            "SubmitQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Ghi nhận đã báo chuyển {submittedAmount:N0} đồng cho {GetPaymentLabel(paymentType)} của đơn #{booking.BookingId}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> ConfirmQrPaymentAsync(
        int paymentId,
        string actorId,
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
        var lateReconciliation =
            BookingWorkflowRules.CanReconcileTransferAfterReservationExpiry(
                booking.Status,
                originalType);
        decimal lateRentalRefund = 0m;
        decimal lateDepositRefund = 0m;

        if (originalType == PaymentType.Rental)
        {
            var requiredRentalAmount = GetRequiredRentalPaymentAmount(booking);
            var rentalPaidBefore = booking.Payments
                .Where(item =>
                    item.Status == PaymentStatus.Paid &&
                    item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
                .Sum(item => item.Amount);
            var outstandingRental = BookingWorkflowRules.CalculateOutstandingRental(
                requiredRentalAmount,
                rentalPaidBefore);

            if (outstandingRental <= 0m)
            {
                return OperationResult.Failure(
                    "Tiền thuê/phí giao đã đủ; không xác nhận thêm giao dịch để tránh thu trùng.");
            }

            if (confirmedAmount != outstandingRental)
            {
                return OperationResult.Failure(
                    $"Giao dịch tiền thuê/phí giao ({confirmedAmount:N0} đồng) không khớp số còn thiếu ({outstandingRental:N0} đồng). Vui lòng xử lý lại trước khi xác nhận.");
            }
        }

        var stateError = ValidateReconciliationState(booking, originalType);
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
            var depositPaidBefore = booking.Payments
                .Where(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.Paid)
                .Sum(item => item.Amount);

            var outstandingDeposit = BookingWorkflowRules.CalculateOutstandingDeposit(
                booking.DepositAmount,
                depositPaidBefore);

            bundledDeposit = booking.Payments
                .FirstOrDefault(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.AwaitingConfirmation);

            if (outstandingDeposit > 0m)
            {
                if (bundledDeposit is null)
                {
                    return OperationResult.Failure(
                        $"Đơn còn thiếu {outstandingDeposit:N0} đồng tiền cọc nhưng không có giao dịch cọc đang chờ đối soát. Không xác nhận tiền thuê để tránh chuyển đơn sang Đã thanh toán sai.");
                }

                if (bundledDeposit.Amount != outstandingDeposit)
                {
                    return OperationResult.Failure(
                        $"Khoản cọc chờ đối soát ({bundledDeposit.Amount:N0} đồng) không khớp phần cọc còn thiếu ({outstandingDeposit:N0} đồng). Vui lòng xử lý lại giao dịch trước khi xác nhận.");
                }

                bundledDeposit.Method = PaymentMethods.BankQr;
                bundledDeposit.Status = PaymentStatus.Paid;
                bundledDeposit.PaidAt = paidAt;
                bundledDeposit.TransactionCode = transactionCode;
            }
            else if (bundledDeposit is not null)
            {
                return OperationResult.Failure(
                    "Tiền cọc của đơn đã đủ nhưng vẫn còn một giao dịch cọc chờ xác nhận. Vui lòng xử lý giao dịch bất thường này trước để tránh thu trùng.");
            }

            var rentalPaid = booking.Payments
                .Where(item =>
                    item.Status == PaymentStatus.Paid &&
                    item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
                .Sum(item => item.Amount);
            var depositPaidAfter = booking.Payments
                .Where(item =>
                    item.Type == PaymentType.Deposit &&
                    item.Status == PaymentStatus.Paid)
                .Sum(item => item.Amount);

            var requiredRentalAmountAfter = GetRequiredRentalPaymentAmount(booking);

            if (!BookingWorkflowRules.HasRequiredUpfrontPayment(
                    requiredRentalAmountAfter,
                    rentalPaid,
                    booking.DepositAmount,
                    depositPaidAfter))
            {
                return OperationResult.Failure(
                    "Chưa đủ tiền thuê hoặc tiền cọc. Không chuyển đơn sang Đã thanh toán.");
            }

            if (lateReconciliation)
            {
                lateRentalRefund = payment.Amount;
                lateDepositRefund = bundledDeposit?.Amount ?? 0m;
            }
            else
            {
                booking.Status = BookingStatus.Paid;
                booking.ReservationExpiresAt = null;
            }
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

            if (lateReconciliation)
            {
                lateRentalRefund = rentalAdjustment;
                lateDepositRefund = depositTopUp;
            }
            else if (booking.Status == BookingStatus.PendingPayment)
            {
                var grossRentalPaidAfterSwap = booking.Payments
                    .Where(item =>
                        item.Status == PaymentStatus.Paid &&
                        item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
                    .Sum(item => item.Amount);
                var rentalRefundPlanned = booking.Payments
                    .Where(item =>
                        item.Type == PaymentType.Refund &&
                        item.Method == PaymentMethods.VehicleSwapRefund &&
                        item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
                    .Sum(item => item.Amount);
                var effectiveRentalPaidAfterSwap = BookingWorkflowRules.CalculateEffectivePaid(
                    grossRentalPaidAfterSwap,
                    rentalRefundPlanned);

                var grossDepositPaidAfterSwap = booking.Payments
                    .Where(item =>
                        item.Type == PaymentType.Deposit &&
                        item.Status == PaymentStatus.Paid)
                    .Sum(item => item.Amount);
                var depositRefundPlanned = booking.Payments
                    .Where(item =>
                        item.Type == PaymentType.Refund &&
                        item.Method == PaymentMethods.DepositRefund &&
                        item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
                    .Sum(item => item.Amount);
                var effectiveDepositPaid = BookingWorkflowRules.CalculateEffectivePaid(
                    grossDepositPaidAfterSwap,
                    depositRefundPlanned);
                var requiredRentalAmount = GetRequiredRentalPaymentAmount(booking);

                if (BookingWorkflowRules.HasRequiredUpfrontPayment(
                        requiredRentalAmount,
                        effectiveRentalPaidAfterSwap,
                        booking.DepositAmount,
                        effectiveDepositPaid))
                {
                    booking.Status = BookingStatus.Paid;
                    booking.ReservationExpiresAt = null;
                }
            }
        }

        var lateRefundTotal = 0m;
        if (lateReconciliation)
        {
            lateRefundTotal = AddLateReconciliationRefunds(
                booking,
                lateRentalRefund,
                lateDepositRefund);
            booking.ReservationExpiresAt = null;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = lateReconciliation
                ? "Đã đối soát tiền chuyển sau khi đơn hết hạn"
                : "Thanh toán đã được xác nhận",
            Message = lateReconciliation
                ? $"Đơn #{booking.BookingId} đã hết thời gian giữ chỗ nên không được khôi phục. SmartCar xác nhận đã nhận {lateRefundTotal:N0} đồng sau khi đơn hết hạn; khoản này đã chuyển sang quy trình chờ Admin duyệt hoàn tiền."
                : originalType switch
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
            actorId,
            lateReconciliation
                ? "ConfirmLateQrPaymentAfterExpiry"
                : "ConfirmQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            lateReconciliation
                ? $"Xác nhận giao dịch đến sau khi đơn #{booking.BookingId} đã hết hạn: thực nhận {lateRefundTotal:N0} đồng; không khôi phục booking và đã tạo khoản chờ hoàn."
                : $"Xác nhận {GetPaymentLabel(originalType)} {confirmedAmount:N0} đồng cho đơn #{booking.BookingId}.",
            cancellationToken: cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RejectQrPaymentAsync(
        int paymentId,
        string actorId,
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

        var expiredBooking = payment.Booking.Status == BookingStatus.Expired;
        payment.Status = expiredBooking
            ? PaymentStatus.Failed
            : PaymentStatus.Pending;
        payment.Method = expiredBooking
            ? PaymentMethods.BankQr
            : PaymentMethods.NotSelected;
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
                bundledDeposit.Status = expiredBooking
                    ? PaymentStatus.Failed
                    : PaymentStatus.Pending;
                bundledDeposit.Method = expiredBooking
                    ? PaymentMethods.BankQr
                    : PaymentMethods.NotSelected;
                bundledDeposit.PaidAt = null;
                bundledDeposit.TransactionCode = null;
            }
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = payment.Booking.CustomerId,
            Title = "Chưa xác nhận được chuyển khoản",
            Message = expiredBooking
                ? $"Đơn #{payment.BookingId} đã hết thời gian giữ chỗ và SmartCar không ghi nhận được tiền cho giao dịch đã báo chuyển. Giao dịch được đóng, booking vẫn hết hạn."
                : $"SmartCar chưa xác nhận được {GetPaymentLabel(payment.Type)} của đơn #{payment.BookingId}. " +
                  "Vui lòng kiểm tra và gửi lại xác nhận."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            actorId,
            "RejectQrPayment",
            nameof(Payment),
            payment.PaymentId.ToString(),
            $"Từ chối xác nhận {GetPaymentLabel(payment.Type)} của đơn #{payment.BookingId}.",
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

    private static string? ValidateReconciliationState(
        Booking booking,
        PaymentType type) =>
        type switch
        {
            PaymentType.Deposit =>
                "Không xác nhận tiền cọc riêng.",

            PaymentType.Rental
                when booking.Status != BookingStatus.PendingPayment &&
                     !BookingWorkflowRules.CanReconcileTransferAfterReservationExpiry(
                         booking.Status,
                         type) =>
                "Đơn không còn ở trạng thái chờ thanh toán trước khi nhận xe.",

            PaymentType.Extension
                when booking.Status != BookingStatus.Rented ||
                     !booking.Extensions.Any(extension => extension.Status == BookingExtensionStatus.Approved) =>
                "Không còn yêu cầu gia hạn đã duyệt đang chờ thanh toán.",

            PaymentType.VehicleSwapAdjustment
                when booking.Status is not (
                    BookingStatus.PendingPayment or
                    BookingStatus.Paid or
                    BookingStatus.ReadyForPickup) &&
                     !BookingWorkflowRules.CanReconcileTransferAfterReservationExpiry(
                         booking.Status,
                         type) =>
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

    private async Task NotifyStaffAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var roleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Staff)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(roleId))
        {
            return;
        }

        var staffIds = await _dbContext.UserRoles
            .Where(item => item.RoleId == roleId)
            .Select(item => item.UserId)
            .ToListAsync(cancellationToken);

        foreach (var staffId in staffIds)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = staffId,
                Title = title,
                Message = message
            });
        }
    }

    private static decimal AddLateReconciliationRefunds(
        Booking booking,
        decimal rentalAmount,
        decimal depositAmount)
    {
        rentalAmount = Math.Max(0m, rentalAmount);
        depositAmount = Math.Max(0m, depositAmount);

        if (rentalAmount > 0m)
        {
            booking.Payments.Add(new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.Refund,
                Amount = rentalAmount,
                Method = PaymentMethods.BankTransferRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        if (depositAmount > 0m)
        {
            booking.Payments.Add(new Payment
            {
                BookingId = booking.BookingId,
                Type = PaymentType.Refund,
                Amount = depositAmount,
                Method = PaymentMethods.DepositRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        var lateRefundTotal = rentalAmount + depositAmount;
        booking.RefundAmount = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                BookingWorkflowRules.CountsTowardRefundTotal(item.Status))
            .Sum(item => item.Amount);

        if (lateRefundTotal > 0m)
        {
            booking.RefundReason = AppendText(
                booking.RefundReason,
                $"Hoàn {lateRefundTotal:N0} đồng đã thực nhận sau khi đơn hết thời gian giữ chỗ; booking không được khôi phục.");
        }

        return lateRefundTotal;
    }

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";

    private static decimal GetRequiredRentalPaymentAmount(Booking booking)
    {
        var deliveryFee = Math.Max(
            0m,
            booking.TotalAmount - booking.RentalAmount - booking.AdditionalAmount);

        if (booking.PickupMethod == VehiclePickupMethod.Delivery &&
            deliveryFee <= 0m &&
            booking.DeliveryLatitude.HasValue &&
            booking.DeliveryLongitude.HasValue)
        {
            deliveryFee = RentalPolicy.CalculateDeliveryFee(
                booking.PickupMethod,
                booking.DeliveryLatitude,
                booking.DeliveryLongitude);
        }

        return Math.Max(0m, booking.RentalAmount + deliveryFee);
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
