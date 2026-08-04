using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Web.Models;

namespace SmartCar.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index(
        string? pickupLocation,
        DateTime? pickupDate,
        string? pickupTime,
        DateTime? returnDate,
        string? returnTime)
    {
        if (pickupDate.HasValue || returnDate.HasValue)
        {
            return RedirectToAction("Index", "Vehicles", new
            {
                pickupLocation,
                pickupDate,
                pickupTime,
                returnDate,
                returnTime
            });
        }

        return View();
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
