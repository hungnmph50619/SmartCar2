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

        // Một yêu cầu gia hạn đã bị từ chối đồng nghĩa thời hạn trả hiện tại vẫn có hiệu lực,
        // không phân biệt trước đó khách chọn gia hạn thường hay bất khả kháng.
        var rejectedExtension = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Where(extension =>
                extension.BookingId == booking.BookingId &&
                extension.Status == BookingExtensionStatus.Rejected)
            .OrderByDescending(extension => extension.BookingExtensionId)
            .Select(extension => new
            {
                extension.BookingExtensionId,
                extension.AdminNote,
                extension.DecidedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (rejectedExtension is null)
            return Json(new { state = "overdue", renterBookingId = booking.BookingId, returnDate = booking.ReturnDate, rejectedExtension = false });

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
                rejectedExtension = true,
                rejectedReason = rejectedExtension.AdminNote,
                rejectedAt = rejectedExtension.DecidedAt
            });
        }

        var compensation = Math.Max(0m, nextBooking.TotalAmount);
        var depositDeduction = Math.Min(availableDeposit, compensation);
        var outstanding = Math.Max(0m, compensation - depositDeduction);
        var affected = DateTime.Now >= nextBooking.PickupDate;

        return Json(new
        {
            state = affected ? "affected" : "overdue-upcoming",
            renterBookingId = booking.BookingId,
            returnDate = booking.ReturnDate,
            availableDeposit,
            rejectedExtension = true,
            rejectedReason = rejectedExtension.AdminNote,
            rejectedAt = rejectedExtension.DecidedAt,
            affectedBookingId = nextBooking.BookingId,
            affectedPickupDate = nextBooking.PickupDate,
            affectedContractAmount = compensation,
            depositDeduction,
            outstandingAmount = outstanding
        });
    }
}
