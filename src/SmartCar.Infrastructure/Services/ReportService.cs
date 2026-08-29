using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Reports;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class ReportService : IReportService
{
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
                item.PaidAt.Value >= from &&
                item.PaidAt.Value < endExclusive &&
                ((item.Status == PaymentStatus.Paid && RevenueTypes.Contains(item.Type)) ||
                 (item.Status == PaymentStatus.Refunded && item.Type == PaymentType.Refund)))
            .Select(item => new
            {
                item.PaymentId,
                item.BookingId,
                item.Booking.VehicleId,
                HasVehicleReturn = item.Booking.VehicleReturn != null,
                item.Booking.Status,
                item.Type,
                item.Amount,
                item.Method,
                OccurredAt = item.PaidAt!.Value
            })
            .ToListAsync(cancellationToken);

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

        var pendingRefunds = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.Refund &&
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
                    item.Method == PaymentMethods.DepositDeduction)
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

            var compensationCost = vehiclePayments
                .Where(item =>
                    item.Type == PaymentType.Refund &&
                    item.Method == PaymentMethods.CompensationRefund)
                .Sum(item => item.Amount);

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
            var incidentCost = vehicleIncidents
                .Sum(item => item.ActualCost + item.FineAmount);

            var netOperatingProfit =
                revenue -
                revenueRefunds -
                maintenanceCost -
                incidentCost -
                compensationCost;

            var transactions = new List<ReportTransactionDto>();

            foreach (var payment in vehiclePayments)
            {
                if (payment.Type == PaymentType.Refund)
                {
                    var refundCategory = payment.Method switch
                    {
                        PaymentMethods.CompensationRefund => "Hỗ trợ/bồi thường",
                        PaymentMethods.VehicleSwapRefund => "Hoàn chênh lệch đổi xe",
                        PaymentMethods.DepositRefund => "Hoàn cọc",
                        _ when payment.HasVehicleReturn => "Hoàn cọc",
                        _ => "Hoàn doanh thu"
                    };

                    transactions.Add(new ReportTransactionDto(
                        payment.OccurredAt,
                        payment.BookingId,
                        refundCategory,
                        refundCategory,
                        payment.Amount,
                        true));
                    continue;
                }

                var category = payment.Type switch
                {
                    PaymentType.Rental => "Tiền thuê",
                    PaymentType.VehicleSwapAdjustment => "Chênh lệch đổi xe",
                    PaymentType.Extension => "Gia hạn",
                    PaymentType.AdditionalCharge when payment.Method == PaymentMethods.DepositDeduction => "Khấu trừ tiền cọc",
                    PaymentType.AdditionalCharge => "Phụ phí",
                    _ => payment.Type.ToString()
                };

                transactions.Add(new ReportTransactionDto(
                    payment.OccurredAt,
                    payment.BookingId,
                    category,
                    category,
                    payment.Amount,
                    false));
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
                    true));
            }

            foreach (var incident in vehicleIncidents)
            {
                var cost = incident.ActualCost + incident.FineAmount;
                if (cost <= 0)
                {
                    continue;
                }

                transactions.Add(new ReportTransactionDto(
                    incident.OccurredAt,
                    incident.BookingId,
                    "Sự cố/phạt",
                    string.IsNullOrWhiteSpace(incident.Description)
                        ? $"Sự cố #{incident.VehicleIncidentId}"
                        : incident.Description,
                    cost,
                    true));
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
            totalRevenueRefunds,
            totalDepositRefunds,
            totalRevenueRefunds + totalDepositRefunds + totalCompensationCost,
            totalMaintenanceCost,
            totalIncidentCost,
            totalCompensationCost,
            totalMaintenanceCost + totalIncidentCost + totalCompensationCost,
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
            rows);
    }

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
}