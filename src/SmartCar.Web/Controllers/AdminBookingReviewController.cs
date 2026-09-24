using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBookingReviewController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IDocumentService _documentService;
    private readonly IConfiguration _configuration;
    private readonly ApplicationDbContext _dbContext;

    public AdminBookingReviewController(
        IBookingService bookingService,
        IDocumentService documentService,
        IConfiguration configuration,
        ApplicationDbContext dbContext)
    {
        _bookingService = bookingService;
        _documentService = documentService;
        _configuration = configuration;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        int id,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(
            id,
            cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status == BookingStatus.PendingConfirmation &&
            !await _dbContext.Bookings.AsNoTracking().AnyAsync(
                item => item.BookingId == id && item.StaffReviewedAt.HasValue,
                cancellationToken)) return NotFound();

        if (booking.Status != BookingStatus.PendingConfirmation)
        {
            return RedirectToAction(
                "Details",
                "AdminBookings",
                new { id });
        }

        ViewBag.CustomerKycVerified =
            await _documentService.HasValidRentalDocumentsAsync(
                booking.CustomerId,
                booking.ReturnDate,
                cancellationToken);

        ViewBag.StoreAddress =
            _configuration["SmartCar:StoreAddress"]
            ?? "SmartCar";

        return View(booking);
    }
}
