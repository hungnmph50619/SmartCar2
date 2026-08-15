using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Services;

public static class ReturnEvidenceHelper
{
    public const string PendingAction = "ReturnEvidencePendingCustomerReview";
    public const string AcceptedAction = "CustomerAcceptedReturnEvidence";
    public const string DisputedAction = "CustomerDisputedReturnEvidence";
    public const string ResolvedAction = "AdminResolvedReturnEvidenceDispute";

    public static readonly string[] ReviewActions =
    {
        PendingAction,
        AcceptedAction,
        DisputedAction,
        ResolvedAction
    };

    public static async Task<CustomerReturnReviewViewModel?> BuildAsync(
        ApplicationDbContext dbContext,
        int bookingId,
        string? customerId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            query = query.Where(item => item.CustomerId == customerId);
        }

        var booking = await query
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

        var checkIn = await VehicleEvidenceAuditHelper.GetCheckInAsync(
            dbContext,
            bookingId,
            cancellationToken);
        var checkOut = await VehicleEvidenceAuditHelper.GetCheckOutAsync(
            dbContext,
            bookingId,
            cancellationToken);

        // Tương thích dữ liệu đã tạo ở bản thử nghiệm chữ ký trước khi chuyển sang check-in bắt buộc.
        var legacyHandoverEvidenceJson = checkIn is null
            ? await dbContext.AuditLogs
                .AsNoTracking()
                .Where(log =>
                    log.Action == "CustomerSignedHandover" &&
                    log.EntityName == nameof(VehicleHandover) &&
                    log.EntityId == bookingId.ToString())
                .OrderByDescending(log => log.CreatedAt)
                .Select(log => log.NewValues)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var (legacyCustomerImages, legacyCustomerNote) = ParseLegacyHandoverEvidence(legacyHandoverEvidenceJson);

        var latestReview = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(VehicleReturn) &&
                log.EntityId == bookingId.ToString() &&
                ReviewActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => new { log.Action, log.NewValues })
            .FirstOrDefaultAsync(cancellationToken);

        var reviewStatus = latestReview?.Action switch
        {
            PendingAction => "Pending",
            AcceptedAction => "Accepted",
            DisputedAction => "Disputed",
            ResolvedAction => "Resolved",
            _ => "Legacy"
        };

        return new CustomerReturnReviewViewModel
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
            CustomerHandoverNote = checkIn?.Note ?? legacyCustomerNote,
            CustomerCheckoutNote = checkOut?.Note,
            CustomerCheckoutMileage = checkOut?.Mileage,
            CustomerCheckoutFuelLevel = checkOut?.FuelLevel,
            ReturnCondition = booking.VehicleReturn.ExteriorCondition,
            ReturnNotes = booking.VehicleReturn.Notes,
            HandoverImagePaths = SplitPaths(booking.Handover.ImagePaths),
            CustomerHandoverImagePaths = checkIn?.ImagePaths ?? legacyCustomerImages,
            CustomerCheckoutImagePaths = checkOut?.ImagePaths ?? Array.Empty<string>(),
            ReturnImagePaths = SplitPaths(booking.VehicleReturn.ImagePaths),
            ReviewStatus = reviewStatus,
            DisputeReason = ParseReason(latestReview?.NewValues)
        };
    }

    private static (IReadOnlyList<string> Images, string? Note) ParseLegacyHandoverEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (Array.Empty<string>(), null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var imagePaths = root.TryGetProperty("CustomerImagePaths", out var imagesElement) &&
                             imagesElement.ValueKind == JsonValueKind.String
                ? imagesElement.GetString()
                : null;
            var note = root.TryGetProperty("CustomerNote", out var noteElement) &&
                       noteElement.ValueKind == JsonValueKind.String
                ? noteElement.GetString()
                : null;
            return (SplitPaths(imagePaths), Normalize(note));
        }
        catch (JsonException)
        {
            return (Array.Empty<string>(), null);
        }
    }

    private static string? ParseReason(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("Reason", out var reasonElement) &&
                   reasonElement.ValueKind == JsonValueKind.String
                ? Normalize(reasonElement.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> SplitPaths(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
