using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff + "," + RoleNames.Admin)]
public sealed class StaffDashboardController : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        if (User.IsInRole(RoleNames.Admin))
        {
            return RedirectToAction("Index", "Dashboard");
        }

        return RedirectToAction("Index", "Staff");
    }
}