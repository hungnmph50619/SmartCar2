using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Reports;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class ReportService : IReportService
{
    private const int VietnamUtcOffsetHours = 7;

    private static readonly BookingStatus[] UtilizedStatuses =
    {
        BookingStatus.Rented,
        BookingStatus.PendingInspection,
        BookingStatus.AwaitingRefund,
        BookingStatus.Completed
    };

    private static readonly BookingStatus[] OpenReceivableStatuses =
    {
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection,
        BookingStatus.AwaitingRefund
    };

    private static readonly PaymentType[] RevenueTypes =
    {
        PaymentType.Rental,
        PaymentType.Extension,
        PaymentType.AdditionalCharge,
        PaymentType.VehicleSwapAdjustment
    };

    // Các khoản này là cọc của khách A được dùng để bồi thường cho khách B.
    // SmartCar chỉ giữ/chuyển hộ, vì vậy không được làm tăng doanh thu hay giảm kết quả ròng.
    private static readonly string[] PassThroughCompensationPrefixes =
    {
        "EXT-ACTUAL-COMP-",
        "EXT-DENIED-COMP-",
        "OVERDUE-COMP-",
        "EXT-COMP-"
    };

    private readonly ApplicationDbContext _dbContext;

    public ReportService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<FleetReportDto> GetFleetReportAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        if (from > to)
        {
            (from, to) = (to, from);
        }

        var endExclusive = to.AddDays(1);
        var periodDays = Math.Max(1, (endExclusive - from).Days);

        // PaidAt được lưu UTC. Khoảng ngày trên màn hình báo cáo là ngày Việt Nam (UTC+7),
        // nên phải đổi mốc 00:00 Việt Nam sang UTC trước khi query.
        var paymentFromUtc = DateTime.SpecifyKind(from.AddHours(-VietnamUtcOffsetHours), DateTimeKind.Utc);
        var paymentEndUtc = DateTime.SpecifyKind(endExclusive.AddHours(-VietnamUtcOffsetHours), DateTimeKind.Utc);

        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .Select(item => new
            {
                item.VehicleId,
                item.VehicleName,
                item.LicensePlate
            })
            .ToListAsync(cancellationToken);

        var bookings = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item =>
                item.PickupDate < endExclusive &&
                item.ReturnDate > from &&
                UtilizedStatuses.Contains(item.Status))
            .Select(item => new
            {
                item.BookingId,
                item.VehicleId,
                item.PickupDate,
                item.ReturnDate,
                item.Status
            })
            .ToListAsync(cancellationToken);

        var periodPayments = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.PaidAt.HasValue &&
                item.PaidAt.Value >= paymentFromUtc &&
                item.PaidAt.Value < paymentEndUtc &&
                ((item.Status == PaymentStatus.Paid && RevenueTypes.Contains(item.Type)) ||
                 (item.Status == PaymentStatus.Refunded && item.Type == PaymentType.Refund)))
            .Select(item => new
            {
                item.PaymentId,
                item.BookingId,
                item.Booking.VehicleId,
                HasVehicleReturn = item.Booking.VehicleReturn != null,
                BookingStatus = item.Booking.Status,
                PaymentStatus = item.Status,
                item.Type,
                item.Amount,
                item.Method,
                item.TransactionCode,
                OccurredAtUtc = item.PaidAt!.Value
            })
            .ToListAsync(cancellationToken);

        // Gắn người thu theo đúng từng giao dịch tiền mặt.
        // Rental cash audit theo Booking; Extension/AdditionalCharge audit theo Payment.
        // Với dữ liệu cũ của phụ phí, EntityId từng lưu BookingId nên ưu tiên khớp thêm mã giao dịch.
        var cashPayments = periodPayments
            .Where(item => item.Type != PaymentType.Refund && item.Method == PaymentMethods.Cash)
            .ToList();
        var cashBookingEntityIds = cashPayments
            .Select(item => item.BookingId.ToString())
            .Distinct()
            .ToArray();
        var cashPaymentEntityIds = cashPayments
            .Select(item => item.PaymentId.ToString())
            .Distinct()
            .ToArray();

        var cashAudits = cashPayments.Count == 0
            ? new List<CashAuditRow>()
            : await _dbContext.AuditLogs
                .AsNoTracking()
                .Where(log =>
                    (log.Action == "StaffReceiveCounterCash" &&
                     log.EntityName == "Booking" &&
                     cashBookingEntityIds.Contains(log.EntityId)) ||
                    ((log.Action == "StaffCollectExtensionCash" ||
                      log.Action == "StaffCollectAdditionalChargeCash") &&
                     log.EntityName == "Payment" &&
                     (cashPaymentEntityIds.Contains(log.EntityId) ||
                      cashBookingEntityIds.Contains(log.EntityId))))
                .Select(log => new CashAuditRow(
                    log.Action,
                    log.EntityName,
                    log.EntityId,
                    log.UserId,
                    log.Description,
                    log.CreatedAt))
                .ToListAsync(cancellationToken);

        var collectorUserByPayment = new Dictionary<int, string>();
        foreach (var payment in cashPayments)
        {
            var transactionCode = payment.TransactionCode?.Trim();

            var audit = cashAudits
                .Where(item => !string.IsNullOrWhiteSpace(item.UserId))
                .Where(item =>
                    payment.Type switch
                    {
                        PaymentType.Rental or PaymentType.VehicleSwapAdjustment =>
                            item.Action == "StaffReceiveCounterCash" &&
                            item.EntityName == "Booking" &&
                            item.EntityId == payment.BookingId.ToString(),

                        PaymentType.Extension =>
                            item.Action == "StaffCollectExtensionCash" &&
                            item.EntityName == "Payment" &&
                            (item.EntityId == payment.PaymentId.ToString() ||
                             (!string.IsNullOrWhiteSpace(transactionCode) &&
                              item.Description.Contains(transactionCode, StringComparison.OrdinalIgnoreCase))),

                        PaymentType.AdditionalCharge =>
                            item.Action == "StaffCollectAdditionalChargeCash" &&
                            item.EntityName == "Payment" &&
                            (item.EntityId == payment.PaymentId.ToString() ||
                             (!string.IsNullOrWhiteSpace(transactionCode) &&
                              item.Description.Contains(transactionCode, StringComparison.OrdinalIgnoreCase))),

                        _ => false
                    })
                .OrderBy(item =>
                    !string.IsNullOrWhiteSpace(transactionCode) &&
                    item.Description.Contains(transactionCode, StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenBy(item => Math.Abs((item.CreatedAt - payment.OccurredAtUtc).TotalSeconds))
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(audit?.UserId))
            {
                collectorUserByPayment[payment.PaymentId] = audit.UserId;
            }
        }

        var collectorUserIds = collectorUserByPayment.Values.Distinct().ToArray();
        var collectorNames = collectorUserIds.Length == 0
            ? new Dictionary<string, string>()
            : await _dbContext.Users
                .AsNoTracking()
                .Where(user => collectorUserIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        var maintenanceRecords = await _dbContext.MaintenanceRecords
            .AsNoTracking()
            .Where(item =>
                item.StartDate < endExclusive &&
                (!item.CompletedDate.HasValue || item.CompletedDate.Value >= from) &&
                item.Status != MaintenanceStatus.Cancelled)
            .Select(item => new
            {
                item.MaintenanceRecordId,
                item.VehicleId,
                item.StartDate,
                item.CompletedDate,
                item.Content,
                item.Cost,
                item.Status
            })
            .ToListAsync(cancellationToken);

        var incidentRecords = await _dbContext.VehicleIncidents
            .AsNoTracking()
            .Where(item =>
                item.OccurredAt >= from &&
                item.OccurredAt < endExclusive &&
                item.Status != IncidentStatus.Cancelled)
            .Select(item => new
            {
                item.VehicleIncidentId,
                item.VehicleId,
                item.BookingId,
                item.OccurredAt,
                item.Description,
                item.ActualCost,
                item.FineAmount,
                item.Status
            })
            .ToListAsync(cancellationToken);

        // Phạt/vi phạm được theo dõi riêng: không phải doanh thu và cũng không phải
        // chi phí SmartCar. Khoản đang mở luôn hiển thị; khoản đã thu theo kỳ PaidAt.
        var trafficFinePayments = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.TrafficFine &&
                item.VehicleIncident != null &&
                item.VehicleIncident.Status != IncidentStatus.Cancelled &&
                (
                    item.Status == PaymentStatus.Pending ||
                    item.Status == PaymentStatus.Failed ||
                    item.Status == PaymentStatus.AwaitingConfirmation ||
                    (item.Status == PaymentStatus.Paid &&
                     item.PaidAt.HasValue &&
                     item.PaidAt.Value >= paymentFromUtc &&
                     item.PaidAt.Value < paymentEndUtc)
                ))
            .Select(item => new
            {
                item.PaymentId,
                item.BookingId,
                VehicleName = item.Booking.Vehicle.VehicleName,
                LicensePlate = item.Booking.Vehicle.LicensePlate,
                ViolationAt = item.VehicleIncident!.OccurredAt,
                item.Amount,
                item.Method,
                item.Status,
                item.TransactionCode,
                PaidAtUtc = item.PaidAt
            })
            .ToListAsync(cancellationToken);

        var paidTrafficFineEntityIds = trafficFinePayments
            .Where(item => item.Status == PaymentStatus.Paid)
            .Select(item => item.PaymentId.ToString())
            .Distinct()
            .ToArray();

        var trafficFineAudits = paidTrafficFineEntityIds.Length == 0
            ? new List<PaymentConfirmationAuditRow>()
            : await _dbContext.AuditLogs
                .AsNoTracking()
                .Where(log =>
                    log.Action == "ConfirmQrPayment" &&
                    log.EntityName == "Payment" &&
                    paidTrafficFineEntityIds.Contains(log.EntityId))
                .Select(log => new PaymentConfirmationAuditRow(
                    log.EntityId,
                    log.UserId,
                    log.CreatedAt))
                .ToListAsync(cancellationToken);

        var trafficFineAuditByPayment = new Dictionary<int, PaymentConfirmationAuditRow>();
        foreach (var audit in trafficFineAudits.OrderByDescending(item => item.CreatedAt))
        {
            if (!int.TryParse(audit.EntityId, out var paymentId) ||
                trafficFineAuditByPayment.ContainsKey(paymentId))
            {
                continue;
            }

            trafficFineAuditByPayment[paymentId] = audit;
        }

        var trafficFineConfirmerIds = trafficFineAuditByPayment.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.UserId))
            .Select(item => item.UserId!)
            .Distinct()
            .ToArray();

        var trafficFineConfirmerNames = trafficFineConfirmerIds.Length == 0
            ? new Dictionary<string, string>()
            : await _dbContext.Users
                .AsNoTracking()
                .Where(user => trafficFineConfirmerIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        var trafficFineReportItems = trafficFinePayments
            .Select(item =>
            {
                trafficFineAuditByPayment.TryGetValue(item.PaymentId, out var audit);
                var confirmedBy = audit?.UserId is { Length: > 0 } userId
                    ? trafficFineConfirmerNames.GetValueOrDefault(userId)
                    : null;
                DateTime? confirmedAt = audit is not null
                    ? ToVietnamTime(audit.CreatedAt)
                    : item.PaidAtUtc.HasValue
                        ? ToVietnamTime(item.PaidAtUtc.Value)
                        : null;

                return new TrafficFineReportItemDto(
                    item.PaymentId,
                    item.BookingId,
                    item.VehicleName,
                    item.LicensePlate,
                    item.ViolationAt,
                    item.Amount,
                    item.Method,
                    item.Status,
                    item.TransactionCode,
                    confirmedBy,
                    confirmedAt);
            })
            .OrderByDescending(item => item.ConfirmedAt ?? item.ViolationAt)
            .ThenByDescending(item => item.PaymentId)
            .Take(10)
            .ToList();

        var pendingTrafficFineAmount = trafficFinePayments
            .Where(item => item.Status is PaymentStatus.Pending or PaymentStatus.Failed)
            .Sum(item => item.Amount);
        var awaitingTrafficFineConfirmationAmount = trafficFinePayments
            .Where(item => item.Status == PaymentStatus.AwaitingConfirmation)
            .Sum(item => item.Amount);
        var collectedTrafficFineAmount = trafficFinePayments
            .Where(item => item.Status == PaymentStatus.Paid)
            .Sum(item => item.Amount);

        var outstandingReceivables = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                RevenueTypes.Contains(item.Type) &&
                item.Method != PaymentMethods.DepositDeduction &&
                (item.Status == PaymentStatus.Pending ||
                 item.Status == PaymentStatus.AwaitingConfirmation) &&
                OpenReceivableStatuses.Contains(item.Booking.Status))
            .SumAsync(item => (decimal?)item.Amount, cancellationToken)
            ?? 0m;

        var outstandingDeposits = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.Deposit &&
                (item.Status == PaymentStatus.Pending ||
                 item.Status == PaymentStatus.AwaitingConfirmation) &&
                OpenReceivableStatuses.Contains(item.Booking.Status))
            .SumAsync(item => (decimal?)item.Amount, cancellationToken)
            ?? 0m;

        var pendingCompensationTransfers = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.CompensationRefund &&
                (item.Status == PaymentStatus.AwaitingRefund ||
                 item.Status == PaymentStatus.RefundApproved))
            .SumAsync(item => (decimal?)item.Amount, cancellationToken)
            ?? 0m;

        var pendingRefunds = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method != PaymentMethods.CompensationRefund &&
                (item.Status == PaymentStatus.AwaitingRefund ||
                 item.Status == PaymentStatus.RefundApproved))
            .SumAsync(item => (decimal?)item.Amount, cancellationToken)
            ?? 0m;

        var paidDeposits = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.Paid)
            .Select(item => new
            {
                item.BookingId,
                item.Booking.Status,
                item.Amount
            })
            .ToListAsync(cancellationToken);

        var depositSettlementRefunds = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.Refund &&
                (item.Status == PaymentStatus.AwaitingRefund ||
                 item.Status == PaymentStatus.RefundApproved ||
                 item.Status == PaymentStatus.Refunded))
            .Select(item => new
            {
                item.BookingId,
                item.Booking.Status,
                HasVehicleReturn = item.Booking.VehicleReturn != null,
                item.Method,
                item.Amount
            })
            .ToListAsync(cancellationToken);

        var allDepositDeductions = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.AdditionalCharge &&
                item.Method == PaymentMethods.DepositDeduction &&
                item.Status == PaymentStatus.Paid)
            .Select(item => new { item.BookingId, item.Amount })
            .ToListAsync(cancellationToken);

        var compensationNotes = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Where(item =>
                item.CustomerNote != null &&
                item.CustomerNote.Contains(CompensationLedger.Marker))
            .Select(item => new { item.BookingId, item.CustomerNote })
            .ToListAsync(cancellationToken);

        var refundsByBooking = depositSettlementRefunds
            .GroupBy(item => item.BookingId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var depositDeductionsByBooking = allDepositDeductions
            .GroupBy(item => item.BookingId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount));
        var compensationReservedByBooking = compensationNotes
            .GroupBy(item => item.BookingId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => CompensationLedger.SumReservedAmount(item.CustomerNote)));

        var depositsHeld = paidDeposits
            .GroupBy(item => item.BookingId)
            .Sum(group =>
            {
                var paid = group.Sum(item => item.Amount);
                refundsByBooking.TryGetValue(group.Key, out var refunds);

                var explicitDepositRefund = refunds?
                    .Where(item => item.Method == PaymentMethods.DepositRefund)
                    .Sum(item => item.Amount) ?? 0m;

                var legacyReturnRefund = refunds?
                    .Where(item =>
                        item.Method != PaymentMethods.VehicleSwapRefund &&
                        item.Method != PaymentMethods.CompensationRefund &&
                        item.Method != PaymentMethods.DepositRefund &&
                        item.HasVehicleReturn)
                    .Sum(item => item.Amount) ?? 0m;

                var bookingStatus = group.Select(item => item.Status).FirstOrDefault();
                var legacyCancelledRefund = explicitDepositRefund > 0 ||
                    bookingStatus is not (BookingStatus.Cancelled or BookingStatus.NoShow or BookingStatus.Rejected)
                    ? 0m
                    : Math.Min(
                        paid,
                        refunds?
                            .Where(item => item.Method != PaymentMethods.CompensationRefund)
                            .Sum(item => item.Amount) ?? 0m);

                var refundedOrReservedDeposit = Math.Min(
                    paid,
                    explicitDepositRefund + legacyReturnRefund + legacyCancelledRefund);
                var deductedDeposit = depositDeductionsByBooking.GetValueOrDefault(group.Key);
                var reservedDeposit = compensationReservedByBooking.GetValueOrDefault(group.Key);
                var unavailableDeposit = Math.Max(deductedDeposit, reservedDeposit);

                return Math.Max(0m, paid - refundedOrReservedDeposit - unavailableDeposit);
            });

        var collectedRevenuePayments = periodPayments
            .Where(item =>
                item.Type != PaymentType.Refund &&
                !IsPassThroughCompensationDeduction(item.Type, item.Method, item.TransactionCode))
            .ToList();

        var totalCashRevenue = collectedRevenuePayments
            .Where(item => item.Method == PaymentMethods.Cash)
            .Sum(item => item.Amount);
        var totalBankQrRevenue = collectedRevenuePayments
            .Where(item => item.Method == PaymentMethods.BankQr)
            .Sum(item => item.Amount);
        var totalDepositDeductionRevenue = collectedRevenuePayments
            .Where(item => item.Method == PaymentMethods.DepositDeduction)
            .Sum(item => item.Amount);
        var totalUnknownPaymentMethodRevenue = collectedRevenuePayments
            .Where(item =>
                item.Method != PaymentMethods.Cash &&
                item.Method != PaymentMethods.BankQr &&
                item.Method != PaymentMethods.DepositDeduction)
            .Sum(item => item.Amount);

        var rows = new List<VehiclePerformanceDto>(vehicles.Count);

        foreach (var vehicle in vehicles)
        {
            var vehiclePayments = periodPayments
                .Where(item => item.VehicleId == vehicle.VehicleId)
                .ToList();

            var rentalRevenue = vehiclePayments
                .Where(item =>
                    item.Type == PaymentType.Rental ||
                    item.Type == PaymentType.VehicleSwapAdjustment)
                .Sum(item => item.Amount);

            var extensionRevenue = vehiclePayments
                .Where(item => item.Type == PaymentType.Extension)
                .Sum(item => item.Amount);

            var additionalChargeRevenue = vehiclePayments
                .Where(item =>
                    item.Type == PaymentType.AdditionalCharge &&
                    item.Method != PaymentMethods.DepositDeduction)
                .Sum(item => item.Amount);

            var depositDeductionRecovery = vehiclePayments
                .Where(item =>
                    item.Type == PaymentType.AdditionalCharge &&
                    item.Method == PaymentMethods.DepositDeduction &&
                    !IsPassThroughCompensationDeduction(item.Type, item.Method, item.TransactionCode))
                .Sum(item => item.Amount);

            var revenue =
                rentalRevenue +
                extensionRevenue +
                additionalChargeRevenue +
                depositDeductionRecovery;

            var revenueRefunds = vehiclePayments
                .Where(item =>
                    item.Type == PaymentType.Refund &&
                    item.Method != PaymentMethods.DepositRefund &&
                    item.Method != PaymentMethods.CompensationRefund &&
                    !(item.HasVehicleReturn && item.Method != PaymentMethods.VehicleSwapRefund))
                .Sum(item => item.Amount);

            var depositRefunds = vehiclePayments
                .Where(item =>
                    item.Type == PaymentType.Refund &&
                    (item.Method == PaymentMethods.DepositRefund ||
                     (item.HasVehicleReturn &&
                      item.Method != PaymentMethods.VehicleSwapRefund &&
                      item.Method != PaymentMethods.CompensationRefund)))
                .Sum(item => item.Amount);

            // CompensationRefund hiện là khoản bồi thường chuyển cho khách B từ nghĩa vụ của khách A.
            // Không phải chi phí SmartCar nên không làm giảm kết quả ròng.
            var compensationCost = 0m;

            var vehicleBookings = bookings
                .Where(item => item.VehicleId == vehicle.VehicleId)
                .ToList();

            var rentalDays = CalculateCoveredDays(
                vehicleBookings.Select(item => (item.PickupDate, item.ReturnDate)),
                from,
                endExclusive,
                periodDays);

            var vehicleMaintenance = maintenanceRecords
                .Where(item => item.VehicleId == vehicle.VehicleId)
                .ToList();

            var maintenanceDays = CalculateCoveredDays(
                vehicleMaintenance.Select(item =>
                    (item.StartDate, item.CompletedDate ?? endExclusive)),
                from,
                endExclusive,
                periodDays);

            var availableDays = Math.Max(0, periodDays - maintenanceDays);
            var fleetUtilizationRate = Math.Min(
                100m,
                Math.Round(rentalDays * 100m / periodDays, 2));
            var availableUtilizationRate = availableDays == 0
                ? 0m
                : Math.Min(
                    100m,
                    Math.Round(rentalDays * 100m / availableDays, 2));

            var maintenanceCost = vehicleMaintenance
                .Where(item =>
                {
                    var recognizedAt = item.CompletedDate ?? item.StartDate;
                    return recognizedAt >= from && recognizedAt < endExclusive;
                })
                .Sum(item => item.Cost);

            var vehicleIncidents = incidentRecords
                .Where(item => item.VehicleId == vehicle.VehicleId)
                .ToList();
            // Tiền phạt giao thông là nghĩa vụ của khách, không phải chi phí vận hành SmartCar.
            // Chỉ ActualCost (chi phí SmartCar thực chịu) mới làm giảm kết quả ròng.
            var incidentCost = vehicleIncidents
                .Sum(item => ReportFinancialRules.IncidentOperatingCost(
                    item.ActualCost,
                    item.FineAmount));

            var netOperatingProfit =
                revenue -
                revenueRefunds -
                maintenanceCost -
                incidentCost -
                compensationCost;

            var transactions = new List<ReportTransactionDto>();

            foreach (var payment in vehiclePayments)
            {
                if (IsPassThroughCompensationDeduction(payment.Type, payment.Method, payment.TransactionCode))
                {
                    continue;
                }

                // Khoản bồi thường cho khách B được theo dõi ở "Tiền đang xử lý",
                // không đưa vào giao dịch doanh thu/chi phí để tránh làm phồng báo cáo.
                if (payment.Type == PaymentType.Refund && payment.Method == PaymentMethods.CompensationRefund)
                {
                    continue;
                }

                var recordedBy = payment.Method == PaymentMethods.Cash &&
                    collectorUserByPayment.TryGetValue(payment.PaymentId, out var collectorUserId)
                    ? collectorNames.GetValueOrDefault(collectorUserId)
                    : null;

                if (payment.Type == PaymentType.Refund)
                {
                    var refundCategory = payment.Method switch
                    {
                        PaymentMethods.VehicleSwapRefund => "Hoàn chênh lệch đổi xe",
                        PaymentMethods.DepositRefund => "Hoàn cọc",
                        _ when payment.HasVehicleReturn => "Hoàn cọc",
                        _ => "Hoàn doanh thu"
                    };

                    transactions.Add(new ReportTransactionDto(
                        ToVietnamTime(payment.OccurredAtUtc),
                        payment.BookingId,
                        refundCategory,
                        refundCategory,
                        payment.Amount,
                        true,
                        payment.Method,
                        payment.TransactionCode,
                        recordedBy));
                    continue;
                }

                var category = payment.Type switch
                {
                    PaymentType.Rental => "Tiền thuê / giao xe",
                    PaymentType.VehicleSwapAdjustment => "Chênh lệch đổi xe",
                    PaymentType.Extension => "Gia hạn",
                    PaymentType.AdditionalCharge when payment.Method == PaymentMethods.DepositDeduction => "Thu phụ phí từ cọc",
                    PaymentType.AdditionalCharge => "Phụ phí",
                    _ => payment.Type.ToString()
                };

                transactions.Add(new ReportTransactionDto(
                    ToVietnamTime(payment.OccurredAtUtc),
                    payment.BookingId,
                    category,
                    category,
                    payment.Amount,
                    false,
                    payment.Method,
                    payment.TransactionCode,
                    recordedBy));
            }

            foreach (var maintenance in vehicleMaintenance)
            {
                var recognizedAt = maintenance.CompletedDate ?? maintenance.StartDate;
                if (maintenance.Cost <= 0 || recognizedAt < from || recognizedAt >= endExclusive)
                {
                    continue;
                }

                transactions.Add(new ReportTransactionDto(
                    recognizedAt,
                    null,
                    "Bảo trì",
                    string.IsNullOrWhiteSpace(maintenance.Content)
                        ? $"Chi phí bảo trì #{maintenance.MaintenanceRecordId}"
                        : maintenance.Content,
                    maintenance.Cost,
                    true,
                    null,
                    null,
                    null));
            }

            foreach (var incident in vehicleIncidents)
            {
                var cost = ReportFinancialRules.IncidentOperatingCost(
                    incident.ActualCost,
                    incident.FineAmount);
                if (cost <= 0)
                {
                    continue;
                }

                transactions.Add(new ReportTransactionDto(
                    incident.OccurredAt,
                    incident.BookingId,
                    "Chi phí sự cố",
                    string.IsNullOrWhiteSpace(incident.Description)
                        ? $"Chi phí sự cố #{incident.VehicleIncidentId}"
                        : incident.Description,
                    cost,
                    true,
                    null,
                    null,
                    null));
            }

            transactions = transactions
                .OrderByDescending(item => item.OccurredAt)
                .ThenByDescending(item => item.Amount)
                .ToList();

            rows.Add(new VehiclePerformanceDto(
                vehicle.VehicleId,
                vehicle.VehicleName,
                vehicle.LicensePlate,
                rentalRevenue,
                extensionRevenue,
                additionalChargeRevenue,
                depositDeductionRecovery,
                revenue,
                revenueRefunds,
                depositRefunds,
                maintenanceCost,
                incidentCost,
                compensationCost,
                netOperatingProfit,
                rentalDays,
                availableDays,
                fleetUtilizationRate,
                availableUtilizationRate,
                vehicleBookings.Count(item => item.Status == BookingStatus.Completed),
                vehicleIncidents.Count,
                rentalDays == 0 ? 0m : Math.Round(revenue / rentalDays, 0),
                transactions));
        }

        rows = rows
            .OrderByDescending(item => item.NetOperatingProfit)
            .ThenByDescending(item => item.FleetUtilizationRate)
            .ToList();

        var totalRevenueRefunds = rows.Sum(item => item.RevenueRefunds);
        var totalDepositRefunds = rows.Sum(item => item.DepositRefunds);
        var totalMaintenanceCost = rows.Sum(item => item.MaintenanceCost);
        var totalIncidentCost = rows.Sum(item => item.IncidentCost);
        var totalCompensationCost = rows.Sum(item => item.CompensationCost);

        return new FleetReportDto(
            from,
            to,
            rows.Sum(item => item.RentalRevenue),
            rows.Sum(item => item.ExtensionRevenue),
            rows.Sum(item => item.AdditionalChargeRevenue),
            rows.Sum(item => item.DepositDeductionRecovery),
            rows.Sum(item => item.Revenue),
            totalCashRevenue,
            totalBankQrRevenue,
            totalDepositDeductionRevenue,
            totalUnknownPaymentMethodRevenue,
            totalRevenueRefunds,
            totalDepositRefunds,
            totalRevenueRefunds + totalDepositRefunds,
            totalMaintenanceCost,
            totalIncidentCost,
            totalCompensationCost,
            totalMaintenanceCost + totalIncidentCost,
            rows.Sum(item => item.NetOperatingProfit),
            rows.Count == 0
                ? 0m
                : Math.Round(rows.Average(item => item.FleetUtilizationRate), 2),
            rows.Count == 0
                ? 0m
                : Math.Round(rows.Average(item => item.AvailableUtilizationRate), 2),
            outstandingReceivables,
            outstandingDeposits,
            depositsHeld,
            pendingRefunds,
            pendingCompensationTransfers,
            pendingTrafficFineAmount,
            awaitingTrafficFineConfirmationAmount,
            collectedTrafficFineAmount,
            trafficFineReportItems,
            rows);
    }

    private static bool IsPassThroughCompensationDeduction(
        PaymentType type,
        string? method,
        string? transactionCode)
    {
        if (type != PaymentType.AdditionalCharge ||
            method != PaymentMethods.DepositDeduction ||
            string.IsNullOrWhiteSpace(transactionCode))
        {
            return false;
        }

        return PassThroughCompensationPrefixes.Any(prefix =>
            transactionCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime ToVietnamTime(DateTime utcValue) => utcValue.AddHours(VietnamUtcOffsetHours);

    private static int CalculateCoveredDays(
        IEnumerable<(DateTime Start, DateTime End)> sourceRanges,
        DateTime from,
        DateTime endExclusive,
        int periodDays)
    {
        var ranges = sourceRanges
            .Select(range =>
            {
                var start = range.Start < from ? from : range.Start;
                var end = range.End > endExclusive ? endExclusive : range.End;
                return (Start: start, End: end);
            })
            .Where(range => range.End > range.Start)
            .OrderBy(range => range.Start)
            .ToList();

        if (ranges.Count == 0)
        {
            return 0;
        }

        var total = TimeSpan.Zero;
        var currentStart = ranges[0].Start;
        var currentEnd = ranges[0].End;

        for (var index = 1; index < ranges.Count; index++)
        {
            var range = ranges[index];
            if (range.Start <= currentEnd)
            {
                if (range.End > currentEnd)
                {
                    currentEnd = range.End;
                }
                continue;
            }

            total += currentEnd - currentStart;
            currentStart = range.Start;
            currentEnd = range.End;
        }

        total += currentEnd - currentStart;

        return Math.Min(
            periodDays,
            Math.Max(0, (int)Math.Ceiling(total.TotalHours / 24d)));
    }

    private sealed record CashAuditRow(
        string Action,
        string EntityName,
        string EntityId,
        string? UserId,
        string Description,
        DateTime CreatedAt);

    private sealed record PaymentConfirmationAuditRow(
        string EntityId,
        string? UserId,
        DateTime CreatedAt);
}
