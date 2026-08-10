using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.Brands;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.Models;

namespace SmartCar.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly IVehicleService _vehicleService;
    private readonly IBrandService _brandService;
    private readonly UserManager<ApplicationUser> _userManager;

    public HomeController(
        ILogger<HomeController> logger,
        IVehicleService vehicleService,
        IBrandService brandService,
        UserManager<ApplicationUser> userManager)
    {
        _logger = logger;
        _vehicleService = vehicleService;
        _brandService = brandService;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Admin làm việc trong workspace quản trị riêng. Không đưa Admin vào giao diện thuê xe của khách.
        if (User.Identity?.IsAuthenticated == true && User.IsInRole(RoleNames.Admin))
        {
            return RedirectToAction("Index", "Dashboard");
        }

        var vehicles = await _vehicleService.GetAllAsync(cancellationToken);
        var featuredVehicles = vehicles
            .Where(vehicle => vehicle.Status == VehicleStatus.Available)
            .OrderBy(vehicle => vehicle.DailyPrice)
            .Take(3)
            .ToArray();

        ViewBag.Brands = new SelectList(
            await _brandService.GetActiveAsync(cancellationToken),
            "BrandId",
            "BrandName");

        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            ViewBag.GreetingName = GetFriendlyName(currentUser?.FullName);
        }

        return View(featuredVehicles);
    }

    public IActionResult Privacy() => View();

    public IActionResult Terms() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }

    private static string? GetFriendlyName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        return fullName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
    }
}
