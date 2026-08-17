namespace SmartCar.Application.Features.Reports;

public sealed record ReportTransactionDto(
    DateTime OccurredAt,
    int? BookingId,
    string Category,
    string Description,
    decimal Amount,
    bool IsOutflow);

public sealed record VehiclePerformanceDto(
    int VehicleId,
    string VehicleName,
    string LicensePlate,
    decimal RentalRevenue,
    decimal ExtensionRevenue,
    decimal AdditionalChargeRevenue,
    decimal Revenue,
    decimal RevenueRefunds,
    decimal DepositRefunds,
    decimal MaintenanceCost,
    decimal IncidentCost,
    decimal NetOperatingProfit,
    int RentalDays,
    int AvailableDays,
    decimal FleetUtilizationRate,
    decimal AvailableUtilizationRate,
    int CompletedBookings,
    int IncidentCount,
    decimal RevenuePerRentalDay,
    IReadOnlyList<ReportTransactionDto> Transactions);

public sealed record FleetReportDto(
    DateTime FromDate,
    DateTime ToDate,
    decimal TotalRentalRevenue,
    decimal TotalExtensionRevenue,
    decimal TotalAdditionalChargeRevenue,
    decimal TotalRevenue,
    decimal TotalRevenueRefunds,
    decimal TotalDepositRefunds,
    decimal TotalCashRefunds,
    decimal TotalMaintenanceCost,
    decimal TotalIncidentCost,
    decimal TotalOperatingCost,
    decimal TotalNetOperatingProfit,
    decimal AverageFleetUtilizationRate,
    decimal AverageAvailableUtilizationRate,
    decimal OutstandingReceivables,
    decimal DepositsHeld,
    decimal PendingRefunds,
    IReadOnlyList<VehiclePerformanceDto> Vehicles);

public interface IReportService
{
    Task<FleetReportDto> GetFleetReportAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default);
}
