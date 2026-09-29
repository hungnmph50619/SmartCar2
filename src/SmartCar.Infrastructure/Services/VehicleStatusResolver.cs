using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal static class VehicleStatusResolver
{
    public static async Task<VehicleStatus> ResolveAsync(
        ApplicationDbContext dbContext,
        Vehicle vehicle,
        int? excludedIncidentId = null,
        CancellationToken cancellationToken = default,
        int? excludedBookingId = null)
    {
        if (vehicle.Status == VehicleStatus.Inactive)
        {
            return VehicleStatus.Inactive;
        }

        var hasActiveRental = await dbContext.Bookings.AnyAsync(booking =>
            booking.VehicleId == vehicle.VehicleId &&
            (!excludedBookingId.HasValue || booking.BookingId != excludedBookingId.Value) &&
            booking.Status == BookingStatus.Rented,
            cancellationToken);
        if (hasActiveRental)
        {
            return VehicleStatus.Rented;
        }

        var hasPendingInspection = await dbContext.Bookings.AnyAsync(booking =>
            booking.VehicleId == vehicle.VehicleId &&
            (!excludedBookingId.HasValue || booking.BookingId != excludedBookingId.Value) &&
            booking.Status == BookingStatus.PendingInspection,
            cancellationToken);
        if (hasPendingInspection)
        {
            return VehicleStatus.Inspection;
        }

        var hasOpenIncident = await dbContext.VehicleIncidents.AnyAsync(incident =>
            incident.VehicleId == vehicle.VehicleId &&
            incident.IncidentType != IncidentType.TrafficFine &&
            (!excludedIncidentId.HasValue ||
             incident.VehicleIncidentId != excludedIncidentId.Value) &&
            incident.Status != IncidentStatus.Resolved &&
            incident.Status != IncidentStatus.Cancelled,
            cancellationToken);

        return hasOpenIncident
            ? VehicleStatus.Maintenance
            : VehicleStatus.Available;
    }
}
