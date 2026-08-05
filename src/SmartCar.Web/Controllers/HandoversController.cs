using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class HandoversController : Controller
{
    private readonly IHandoverService _handoverService;

    public HandoversController(IHandoverService handoverService)
    {
        _handoverService = handoverService;
    }

    [HttpGet]
    public IActionResult Create(int bookingId)
    {
        return View(new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = DateTime.Now
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _handoverService.CreateAsync(
            new CreateHandoverRequest(
                model.BookingId,
                model.HandoverAt,
                model.Mileage,
                model.FuelLevel,
                model.ExteriorCondition,
                model.InteriorCondition,
                model.Accessories,
                model.ImagePaths,
                model.Notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        TempData["SuccessMessage"] = "Đã lập biên bản giao xe.";
        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }
}
