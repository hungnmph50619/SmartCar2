using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public AdminBookingReviewController(
        IBookingService bookingService,
        IDocumentService documentService,
        IConfiguration configuration)
    {
        _bookingService = bookingService;
        _documentService = documentService;
        _configuration = configuration;
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
