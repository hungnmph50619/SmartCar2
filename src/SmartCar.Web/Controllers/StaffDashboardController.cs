using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff + "," + RoleNames.Admin)]
public sealed class StaffDashboardController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public StaffDashboardController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);

        if (user is null || !user.IsActive)
        {
            await _signInManager.SignOutAsync();

            return RedirectToAction("Login", "Account");
        }

        ViewBag.StaffName = user.FullName;

        return View();
    }
}