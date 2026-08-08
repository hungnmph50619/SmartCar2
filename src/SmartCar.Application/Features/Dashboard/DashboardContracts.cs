using SmartCar.Application.Features.Bookings;

namespace SmartCar.Application.Features.Dashboard;

public sealed class DashboardDto
{
    public int TotalVehicles { get; init; }
    public int AvailableVehicles { get; init; }
    public int RentedVehicles { get; init; }
    public int InspectionVehicles { get; init; }
    public int MaintenanceVehicles { get; init; }
    public int PendingBookings { get; init; }
    public int PendingKycPackages { get; init; }
    public int TodayPickups { get; init; }
    public int TodayReturns { get; init; }
    public int ActiveRentals { get; init; }
    public decimal MonthlyRevenue { get; init; }
    public IReadOnlyList<BookingListItemDto> RecentBookings { get; init; } = Array.Empty<BookingListItemDto>();
}

public interface IDashboardService
{
    Task<DashboardDto> GetAsync(CancellationToken cancellationToken = default);
}
