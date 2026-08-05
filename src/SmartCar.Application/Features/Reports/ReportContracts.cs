namespace SmartCar.Application.Features.Reports;

public sealed record VehiclePerformanceDto(
    int VehicleId,
    string VehicleName,
    string LicensePlate,
    decimal Revenue,
    decimal Refunds,
    decimal MaintenanceCost,
    decimal IncidentCost,
    decimal NetProfit,
    int RentalDays,
    decimal UtilizationRate,
    int CompletedBookings,
    int IncidentCount);

public sealed record FleetReportDto(
    DateTime FromDate,
    DateTime ToDate,
    decimal TotalRevenue,
    decimal TotalRefunds,
    decimal TotalOperatingCost,
    decimal TotalNetProfit,
    decimal AverageUtilizationRate,
    IReadOnlyList<VehiclePerformanceDto> Vehicles);

public interface IReportService
{
    Task<FleetReportDto> GetFleetReportAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default);
}
