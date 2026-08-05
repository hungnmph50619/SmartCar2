using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Enums;
using SmartCar.Web.Models;

namespace SmartCar.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IVehicleService _vehicleService;

    public HomeController(
        ILogger<HomeController> logger,
        IVehicleService vehicleService)
    {
        _logger = logger;
        _vehicleService = vehicleService;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var vehicles = await _vehicleService.GetAllAsync(cancellationToken);
        var featuredVehicles = vehicles
            .Where(vehicle => vehicle.Status == VehicleStatus.Available)
            .OrderBy(vehicle => vehicle.DailyPrice)
            .Take(3)
            .ToArray();

        return View(featuredVehicles);
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }
}
