using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminOverdueConflictsController : Controller
{
    private const string ForceMajeureMarker = "[FORCE_MAJEURE]";

    private static readonly BookingStatus[] BlockingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;

    public AdminOverdueConflictsController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Status(int bookingId, CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
            return NotFound();

        if (booking.Status != BookingStatus.Rented || DateTime.Now <= booking.ReturnDate)
            return Json(new { state = "none" });

        var rejectedNormalExtension = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Where(extension =>
                extension.BookingId == booking.BookingId &&
                extension.Status == BookingExtensionStatus.Rejected &&
                (extension.CustomerNote == null || !extension.CustomerNote.Contains(ForceMajeureMarker)))
            .OrderByDescending(extension => extension.BookingExtensionId)
            .Select(extension => new
            {
                extension.BookingExtensionId,
                extension.AdminNote
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (rejectedNormalExtension is null)
            return Json(new { state = "none" });

        var nextBooking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item =>
                item.VehicleId == booking.VehicleId &&
                item.BookingId != booking.BookingId &&
                item.PickupDate >= booking.ReturnDate &&
                BlockingStatuses.Contains(item.Status))
            .OrderBy(item => item.PickupDate)
            .Select(item => new
            {
                item.BookingId,
                item.PickupDate,
                item.TotalAmount
            })
            .FirstOrDefaultAsync(cancellationToken);

        var depositPaid = booking.Payments
            .Where(payment => payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var depositRefundedOrPlanned = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Method == PaymentMethods.DepositRefund &&
                payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.Refunded)
            .Sum(payment => payment.Amount);

        var depositAlreadyDeducted = booking.Payments
            .Where(payment =>
                payment.Type == PaymentType.AdditionalCharge &&
                payment.Method == PaymentMethods.DepositDeduction &&
                payment.Status == PaymentStatus.Paid)
            .Sum(payment => payment.Amount);

        var availableDeposit = Math.Max(0m, depositPaid - depositRefundedOrPlanned - depositAlreadyDeducted);

        if (nextBooking is null)
        {
            return Json(new
            {
                state = "overdue",
                renterBookingId = booking.BookingId,
                returnDate = booking.ReturnDate,
                availableDeposit,
                rejectedReason = rejectedNormalExtension.AdminNote
            });
        }

        var affected = DateTime.Now >= nextBooking.PickupDate;
        return Json(new
        {
            state = affected ? "affected" : "overdue-upcoming",
            renterBookingId = booking.BookingId,
            returnDate = booking.ReturnDate,
            availableDeposit,
            rejectedReason = rejectedNormalExtension.AdminNote,
            affectedBookingId = nextBooking.BookingId,
            affectedPickupDate = nextBooking.PickupDate,
            affectedContractAmount = nextBooking.TotalAmount
        });
    }
}
