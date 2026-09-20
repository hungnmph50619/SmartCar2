namespace SmartCar.Application.Features.Reports;

public static class ReportFinancialRules
{
    /// <summary>
    /// Only costs actually borne by SmartCar reduce operating profit.
    /// Traffic fines charged to the renter are tracked separately and are not SmartCar operating costs.
    /// </summary>
    public static decimal IncidentOperatingCost(decimal actualCost, decimal fineAmount)
        => Math.Max(0m, actualCost);
}
