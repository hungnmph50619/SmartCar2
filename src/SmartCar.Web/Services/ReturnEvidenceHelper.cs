using Microsoft.EntityFrameworkCore;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Services;

public static class ReturnEvidenceHelper
{
    private const string SignatureRefusedAction = "ReturnDocumentSignatureRefused";
    private const string SignatureResolvedAction = "ReturnDocumentSignatureDisputeResolved";

    public static async Task<RentalEvidenceViewModel?> BuildAsync(
        ApplicationDbContext dbContext,
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.Handover is null || booking.VehicleReturn is null)
        {
            return null;
        }

        var customerName = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == booking.CustomerId)
            .Select(user => user.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Khách thuê";

        var bookingKey = bookingId.ToString();
        var refusal = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == SignatureRefusedAction &&
                log.EntityName == "Booking" &&
                log.EntityId == bookingKey)
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => new { log.CreatedAt, log.Description })
            .FirstOrDefaultAsync(cancellationToken);

        var resolution = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == SignatureResolvedAction &&
                log.EntityName == "Booking" &&
                log.EntityId == bookingKey)
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => new { log.CreatedAt, log.Description })
            .FirstOrDefaultAsync(cancellationToken);

        var disputeActive = refusal is not null &&
                            (resolution is null || resolution.CreatedAt < refusal.CreatedAt);
        var resolvedAfterRefusal = refusal is not null &&
                                   resolution is not null &&
                                   resolution.CreatedAt >= refusal.CreatedAt;

        var handoverPaths = SplitPaths(booking.Handover.ImagePaths);
        var returnPaths = SplitPaths(booking.VehicleReturn.ImagePaths);

        return new RentalEvidenceViewModel
        {
            BookingId = booking.BookingId,
            CustomerName = customerName,
            VehicleName = booking.Vehicle.VehicleName,
            LicensePlate = booking.Vehicle.LicensePlate,
            HandoverAt = booking.Handover.HandoverAt,
            ReturnedAt = booking.VehicleReturn.ReturnedAt,
            HandoverMileage = booking.Handover.Mileage,
            ReturnMileage = booking.VehicleReturn.Mileage,
            HandoverFuelLevel = booking.Handover.FuelLevel,
            ReturnFuelLevel = booking.VehicleReturn.FuelLevel,
            HandoverNotes = booking.Handover.Notes,
            ReturnCondition = booking.VehicleReturn.ExteriorCondition,
            ReturnNotes = booking.VehicleReturn.Notes,
            HandoverVehicleImagePaths = handoverPaths
                .Where(path => !path.Contains("/handover-documents/", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            HandoverDocumentImagePaths = handoverPaths
                .Where(path => path.Contains("/handover-documents/", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            ReturnVehicleImagePaths = returnPaths
                .Where(path => !path.Contains("/return-documents/", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            ReturnDocumentImagePaths = returnPaths
                .Where(path => path.Contains("/return-documents/", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            CustomerSignedReturnDocument = refusal is null,
            SignatureDisputeActive = disputeActive,
            SignatureRefusalReason = refusal?.Description,
            SignatureDisputeResolution = resolvedAfterRefusal ? resolution!.Description : null
        };
    }

    public static IReadOnlyList<string> SplitPaths(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
