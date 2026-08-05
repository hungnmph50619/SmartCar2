using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Reports;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class ReportService : IReportService
{
    private static readonly BookingStatus[] UtilizedStatuses =
    {
        BookingStatus.Rented,
        BookingStatus.PendingInspection,
        BookingStatus.Completed
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

        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.Status == PaymentStatus.Paid &&
                item.PaidAt.HasValue &&
                item.PaidAt.Value >= from &&
                item.PaidAt.Value < endExclusive)
            .Select(item => new
            {
                item.Booking.VehicleId,
                item.Type,
                item.Amount
            })
            .ToListAsync(cancellationToken);

        var maintenanceCosts = await _dbContext.MaintenanceRecords
            .AsNoTracking()
            .Where(item =>
                item.StartDate < endExclusive &&
                (!item.CompletedDate.HasValue || item.CompletedDate.Value >= from) &&
                item.Status != MaintenanceStatus.Cancelled)
            .GroupBy(item => item.VehicleId)
            .Select(group => new
            {
                VehicleId = group.Key,
                Cost = group.Sum(item => item.Cost)
            })
            .ToDictionaryAsync(item => item.VehicleId, item => item.Cost, cancellationToken);

        var incidentCosts = await _dbContext.VehicleIncidents
            .AsNoTracking()
            .Where(item =>
                item.OccurredAt >= from &&
                item.OccurredAt < endExclusive &&
                item.Status != IncidentStatus.Cancelled)
            .GroupBy(item => item.VehicleId)
            .Select(group => new
            {
                VehicleId = group.Key,
                Cost = group.Sum(item => item.ActualCost + item.FineAmount),
                Count = group.Count()
            })
            .ToListAsync(cancellationToken);

        var incidentCostMap = incidentCosts.ToDictionary(item => item.VehicleId, item => item.Cost);
        var incidentCountMap = incidentCosts.ToDictionary(item => item.VehicleId, item => item.Count);

        var rows = new List<VehiclePerformanceDto>(vehicles.Count);
        foreach (var vehicle in vehicles)
        {
            var vehiclePayments = payments.Where(item => item.VehicleId == vehicle.VehicleId).ToList();
            var revenue = vehiclePayments
                .Where(item => item.Type is PaymentType.Rental or PaymentType.Extension or PaymentType.AdditionalCharge)
                .Sum(item => item.Amount);
            var refunds = vehiclePayments
                .Where(item => item.Type == PaymentType.Refund)
                .Sum(item => item.Amount);

            var vehicleBookings = bookings.Where(item => item.VehicleId == vehicle.VehicleId).ToList();
            var rentalDays = vehicleBookings.Sum(item =>
            {
                var overlapStart = item.PickupDate < from ? from : item.PickupDate;
                var overlapEnd = item.ReturnDate > endExclusive ? endExclusive : item.ReturnDate;
                return Math.Max(0, (int)Math.Ceiling((overlapEnd - overlapStart).TotalHours / 24d));
            });

            var utilizationRate = Math.Min(100m, Math.Round(rentalDays * 100m / periodDays, 2));
            var maintenanceCost = maintenanceCosts.GetValueOrDefault(vehicle.VehicleId);
            var incidentCost = incidentCostMap.GetValueOrDefault(vehicle.VehicleId);
            var netProfit = revenue - refunds - maintenanceCost - incidentCost;

            rows.Add(new VehiclePerformanceDto(
                vehicle.VehicleId,
                vehicle.VehicleName,
                vehicle.LicensePlate,
                revenue,
                refunds,
                maintenanceCost,
                incidentCost,
                netProfit,
                rentalDays,
                utilizationRate,
                vehicleBookings.Count(item => item.Status == BookingStatus.Completed),
                incidentCountMap.GetValueOrDefault(vehicle.VehicleId)));
        }

        rows = rows
            .OrderByDescending(item => item.NetProfit)
            .ThenByDescending(item => item.UtilizationRate)
            .ToList();

        return new FleetReportDto(
            from,
            to,
            rows.Sum(item => item.Revenue),
            rows.Sum(item => item.Refunds),
            rows.Sum(item => item.MaintenanceCost + item.IncidentCost),
            rows.Sum(item => item.NetProfit),
            rows.Count == 0 ? 0 : Math.Round(rows.Average(item => item.UtilizationRate), 2),
            rows);
    }
}
