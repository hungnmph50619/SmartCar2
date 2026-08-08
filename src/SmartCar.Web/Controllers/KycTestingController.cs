using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class KycTestingController : Controller
{
    private readonly KycTestingService _testing;

    public KycTestingController(KycTestingService testing)
    {
        _testing = testing;
    }

    [HttpGet("/Ekyc/TestModeStatus")]
    public IActionResult Status() => Json(new
    {
        available = _testing.Available,
        active = _testing.IsActive(HttpContext),
        environment = _testing.Available ? "Development" : null
    });

    [HttpPost("/Ekyc/SetTestMode")]
    [ValidateAntiForgeryToken]
    public IActionResult SetMode(bool enabled)
    {
        if (!_testing.Available)
        {
            return NotFound();
        }

        if (enabled)
        {
            Response.Cookies.Append(
                KycTestingService.CookieName,
                "1",
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    IsEssential = true,
                    MaxAge = TimeSpan.FromHours(8)
                });
        }
        else
        {
            Response.Cookies.Delete(KycTestingService.CookieName);
        }

        return Json(new { succeeded = true, active = enabled });
    }
}
