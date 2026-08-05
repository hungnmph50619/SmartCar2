using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.Brands;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Enums;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[AllowAnonymous]
public sealed class VehiclesController : Controller
{
    private readonly IVehicleService _vehicleService;
    private readonly IBrandService _brandService;

    public VehiclesController(IVehicleService vehicleService, IBrandService brandService)
    {
        _vehicleService = vehicleService;
        _brandService = brandService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] VehicleSearchViewModel model,
        CancellationToken cancellationToken)
    {
        await LoadBrandsAsync(model.BrandId, cancellationToken);
        ViewBag.Search = model;

        if (model.PickupDate >= model.ReturnDate)
        {
            ModelState.AddModelError(string.Empty,
                "Ngày giờ nhận xe phải trước ngày giờ trả xe.");
            return View(Array.Empty<VehicleDto>());
        }

        var vehicles = await _vehicleService.SearchAvailableAsync(
            new VehicleSearchRequest(
                model.PickupDate,
                model.ReturnDate,
                model.BrandId,
                model.Seats,
                model.Transmission,
                model.MaxDailyPrice),
            cancellationToken);

        return View(vehicles);
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        int id,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleService.GetByIdAsync(id, cancellationToken);
        if (vehicle is null || vehicle.Status == VehicleStatus.Inactive)
        {
            return NotFound();
        }

        ViewBag.PickupDate = pickupDate ?? DateTime.Now.AddDays(1);
        ViewBag.ReturnDate = returnDate ?? DateTime.Now.AddDays(2);
        return View(vehicle);
    }

    private async Task LoadBrandsAsync(int? selectedId, CancellationToken cancellationToken)
    {
        ViewBag.Brands = new SelectList(
            await _brandService.GetActiveAsync(cancellationToken),
            "BrandId",
            "BrandName",
            selectedId);
    }
}
