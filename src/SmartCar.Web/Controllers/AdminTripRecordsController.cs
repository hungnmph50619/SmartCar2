using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
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

        // Nếu xe thực tế được trả sau giờ nhận của đơn kế tiếp, lưu ID đơn bị ảnh hưởng
        // để phần quyết toán giải thích rõ khoản cọc bị giữ lại/bồi thường.
        ViewBag.AffectedBookingId = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.VehicleId == records.VehicleId
                && item.BookingId != records.BookingId
                && item.PickupDate > records.ReturnDate
                && item.PickupDate < records.VehicleReturn.ReturnedAt)
            .OrderBy(item => item.PickupDate)
            .Select(item => (int?)item.BookingId)
            .FirstOrDefaultAsync(cancellationToken);

        var handoverPaths = SplitPaths(records.Handover.ImagePaths);
        var returnPaths = SplitPaths(records.VehicleReturn.ImagePaths);

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
                SignedDocumentPath = handoverPaths
                    .FirstOrDefault(path => path.Contains(HandoverSignedMarker, StringComparison.OrdinalIgnoreCase))
            },
            Return = new ReturnDocumentViewModel
            {
                Booking = booking,
                RecordedAt = records.VehicleReturn.ReturnedAt,
                Mileage = records.VehicleReturn.Mileage,
                FuelLevel = records.VehicleReturn.FuelLevel,
                HasDamage = records.VehicleReturn.HasDamage,
                IsLateReturn = records.VehicleReturn.IsLateReturn,
                LateMinutes = records.VehicleReturn.LateMinutes,
                LateFee = records.VehicleReturn.LateFee,
                Notes = records.VehicleReturn.Notes,
                ImagePaths = returnPaths
                    .Where(path => !path.Contains(ReturnSignedMarker, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                SignedDocumentPath = returnPaths
                    .FirstOrDefault(path => path.Contains(ReturnSignedMarker, StringComparison.OrdinalIgnoreCase))
            }
        };

        return View(model);
    }

    private static IReadOnlyList<string> SplitPaths(string? paths) =>
        string.IsNullOrWhiteSpace(paths)
            ? Array.Empty<string>()
            : paths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
}
