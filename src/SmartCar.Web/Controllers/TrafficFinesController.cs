using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class TrafficFinesController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IConfiguration _configuration;

    public TrafficFinesController(
        ApplicationDbContext dbContext,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? bookingId, CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        if (bookingId.HasValue && !await _dbContext.Bookings.AsNoTracking().AnyAsync(
                booking => booking.BookingId == bookingId.Value && booking.CustomerId == customerId,
                cancellationToken))
        {
            return NotFound();
        }
        ViewBag.SelectedBookingId = bookingId;

        var items = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Booking.CustomerId == customerId &&
                payment.Type == PaymentType.TrafficFine &&
                (!bookingId.HasValue || payment.BookingId == bookingId.Value))
            .OrderByDescending(payment => payment.PaymentId)
            .Select(payment => new TrafficFinePaymentViewModel
            {
                PaymentId = payment.PaymentId,
                BookingId = payment.BookingId,
                VehicleIncidentId = payment.VehicleIncidentId,
                VehicleName = payment.Booking.Vehicle.VehicleName,
                LicensePlate = payment.Booking.Vehicle.LicensePlate,
                OccurredAt = payment.VehicleIncident != null
                    ? payment.VehicleIncident.OccurredAt
                    : null,
                Description = payment.VehicleIncident != null
                    ? payment.VehicleIncident.Description
                    : null,
                EvidencePaths = payment.VehicleIncident != null
                    ? payment.VehicleIncident.EvidencePaths
                    : null,
                OfficialFineAmount = payment.VehicleIncident != null
                    ? payment.VehicleIncident.FineAmount
                    : payment.Amount,
                Amount = payment.Amount,
                Status = payment.Status,
                PaidAt = payment.PaidAt,
                TransactionCode = payment.TransactionCode
            })
            .ToListAsync(cancellationToken);

        return View(new TrafficFineIndexViewModel
        {
            Items = items,
            BankName = _configuration["PaymentQr:BankName"] ?? "MB Bank",
            AccountNumber = _configuration["PaymentQr:AccountNumber"] ?? "0123456789",
            AccountHolder = _configuration["PaymentQr:AccountHolder"] ?? "SMARTCAR",
            QrImagePath = _configuration["PaymentQr:QrImagePath"] ?? "/images/payment/bank-qr.jpg"
        });
    }
}

