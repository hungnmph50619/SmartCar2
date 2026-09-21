using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminTripRecordsController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly IBookingService _bookingService;
    private readonly ApplicationDbContext _dbContext;

    public AdminTripRecordsController(
        IBookingService bookingService,
        ApplicationDbContext dbContext)
    {
        _bookingService = bookingService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        int id,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(id, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var records = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == id, cancellationToken);

        if (records?.Handover is null || records.VehicleReturn is null)
        {
            TempData["ErrorMessage"] = "Hồ sơ chuyến chỉ có khi đã có cả biên bản giao và trả xe.";
            return RedirectToAction("Details", "AdminBookings", new { id });
        }

        const string overdueCompPrefix = "OVERDUE-COMP-";
        const string overdueDebtPrefix = "OVERDUE-DEBT-";

        var overduePayments = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.BookingId == id &&
                payment.TransactionCode != null &&
                (
                    (payment.Type == PaymentType.AdditionalCharge &&
                     payment.Method == PaymentMethods.DepositDeduction &&
                     payment.TransactionCode.StartsWith(overdueCompPrefix)) ||
                    (payment.Type == PaymentType.OverdueCompensationDebt &&
                     payment.TransactionCode.StartsWith(overdueDebtPrefix))
                ))
            .Select(payment => new
            {
                payment.Type,
                payment.Amount,
                payment.Status,
                payment.TransactionCode
            })
            .ToListAsync(cancellationToken);

        var overdueSettlements = overduePayments
            .Select(payment => new
            {
                AffectedBookingId = ParseAffectedBookingId(
                    payment.TransactionCode,
                    payment.Type == PaymentType.OverdueCompensationDebt
                        ? overdueDebtPrefix
                        : overdueCompPrefix),
                payment.Type,
                payment.Amount,
                payment.Status
            })
            .Where(item => item.AffectedBookingId.HasValue)
            .GroupBy(item => item.AffectedBookingId!.Value)
            .Select(group => new OverdueSettlementViewModel(
                group.Key,
                group
                    .Where(item => item.Type == PaymentType.AdditionalCharge)
                    .Sum(item => item.Amount),
                group
                    .Where(item => item.Type == PaymentType.OverdueCompensationDebt)
                    .Sum(item => item.Amount),
                group
                    .Where(item =>
                        item.Type == PaymentType.OverdueCompensationDebt &&
                        item.Status == PaymentStatus.Paid)
                    .Sum(item => item.Amount),
                group
                    .Where(item =>
                        item.Type == PaymentType.OverdueCompensationDebt &&
                        item.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation)
                    .Sum(item => item.Amount)))
            .OrderBy(item => item.AffectedBookingId)
            .ToList();

        var handoverPaths = SplitPaths(records.Handover.ImagePaths);
        var returnPaths = SplitPaths(records.VehicleReturn.ImagePaths);
        var signedHandoverPaths = handoverPaths
            .Where(path => path.Contains(HandoverSignedMarker, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var signedReturnPaths = returnPaths
            .Where(path => path.Contains(ReturnSignedMarker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var model = new TripRecordViewModel
        {
            Booking = booking,
            Handover = new HandoverDocumentViewModel
            {
                Booking = booking,
                RecordedAt = records.Handover.HandoverAt,
                Mileage = records.Handover.Mileage,
                FuelLevel = records.Handover.FuelLevel,
                Notes = records.Handover.Notes,
                IncludedKilometers = records.Handover.IncludedKilometers,
                ExcessKmFeePerKm = records.Handover.ExcessKmFeePerKm,
                LateReturnFeeMultiplier = records.Handover.LateReturnFeeMultiplier,
                TrafficFineTerms = records.Handover.TrafficFineTerms,
                DamageCompensationTerms = records.Handover.DamageCompensationTerms,
                PenaltyPolicyAccepted = records.Handover.PenaltyPolicyAccepted,
                ImagePaths = handoverPaths
                    .Where(path => !path.Contains(HandoverSignedMarker, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                SignedDocumentPaths = signedHandoverPaths,
                SignedDocumentPath = signedHandoverPaths.FirstOrDefault(),
                SignedDocumentVerified = records.Handover.SignedDocumentVerified,
                SignedDocumentVerifiedAt = records.Handover.SignedDocumentVerifiedAt
            },
            Return = new ReturnDocumentViewModel
            {
                Booking = booking,
                RecordedAt = records.VehicleReturn.ReturnedAt,
                Mileage = records.VehicleReturn.Mileage,
                FuelLevel = records.VehicleReturn.FuelLevel,
                ExteriorCondition = records.VehicleReturn.ExteriorCondition,
                InteriorCondition = records.VehicleReturn.InteriorCondition,
                Accessories = records.VehicleReturn.AccessoryStatus,
                HasDamage = records.VehicleReturn.HasDamage,
                IsLateReturn = records.VehicleReturn.IsLateReturn,
                LateMinutes = records.VehicleReturn.LateMinutes,
                LateFee = records.VehicleReturn.LateFee,
                Notes = records.VehicleReturn.Notes,
                ImagePaths = returnPaths
                    .Where(path => !path.Contains(ReturnSignedMarker, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                SignedDocumentPaths = signedReturnPaths,
                SignedDocumentPath = signedReturnPaths.FirstOrDefault(),
                SignedDocumentVerified = records.VehicleReturn.SignedDocumentVerified,
                SignedDocumentVerifiedAt = records.VehicleReturn.SignedDocumentVerifiedAt
            },
            OverdueSettlements = overdueSettlements
        };

        return View(model);
    }

    private static int? ParseAffectedBookingId(
        string? transactionCode,
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(transactionCode) ||
            !transactionCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = transactionCode[prefix.Length..]
            .Split('-', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 &&
               int.TryParse(parts[1], out var affectedBookingId)
            ? affectedBookingId
            : null;
    }

    private static IReadOnlyList<string> SplitPaths(string? paths) =>
        string.IsNullOrWhiteSpace(paths)
            ? Array.Empty<string>()
            : paths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
}
 
