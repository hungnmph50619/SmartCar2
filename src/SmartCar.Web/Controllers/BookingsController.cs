using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class BookingsController : Controller
{
    private readonly IBookingService _bookingService;

    public BookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        CreateBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var result = await _bookingService.CreateAsync(
            customerId,
            new CreateBookingRequest(model.VehicleId, model.PickupDate, model.ReturnDate),
            cancellationToken);

        if (!result.Succeeded || !result.BookingId.HasValue)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToAction("Details", "Vehicles", new
            {
                id = model.VehicleId,
                pickupDate = model.PickupDate,
                returnDate = model.ReturnDate
            });
        }

        TempData["SuccessMessage"] = "Đã gửi yêu cầu thuê xe. Vui lòng chờ Admin xác nhận.";
        return RedirectToAction(nameof(Details), new { id = result.BookingId.Value });
    }

    [HttpGet]
    public async Task<IActionResult> MyBookings(CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        return View(await _bookingService.GetCustomerBookingsAsync(
            customerId,
            cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking = await _bookingService.GetCustomerBookingAsync(
            id,
            customerId,
            cancellationToken);

        return booking is null ? NotFound() : View(booking);
    }
}
