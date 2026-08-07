using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class BookingsController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IDocumentService _documentService;

    public BookingsController(
        IBookingService bookingService,
        IDocumentService documentService)
    {
        _bookingService = bookingService;
        _documentService = documentService;
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

        var hasValidRentalDocuments = await _documentService.HasValidRentalDocumentsAsync(
            customerId,
            model.ReturnDate,
            cancellationToken);

        if (!hasValidRentalDocuments)
        {
            TempData["ErrorMessage"] =
                "Bạn cần hoàn tất xác minh CCCD mặt trước, CCCD mặt sau và GPLX còn hiệu lực đến ngày trả xe trước khi gửi yêu cầu thuê.";
            TempData["KycRequired"] = "true";
            return RedirectToAction(
                "Index",
                "Profile",
                new { tab = "documents" });
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
