using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class ProfileController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _auditService;

    public ProfileController(
        UserManager<ApplicationUser> userManager,
        IAuditService auditService)
    {
        _userManager = userManager;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        return View(ToViewModel(user));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        ProfileViewModel model,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        model.Email = user.Email ?? string.Empty;
        model.CreatedAt = user.CreatedAt;

        if (!ModelState.IsValid)
        {
            return View("Index", model);
        }

        user.FullName = model.FullName.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber)
            ? null
            : model.PhoneNumber.Trim();
        user.Address = string.IsNullOrWhiteSpace(model.Address)
            ? null
            : model.Address.Trim();

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View("Index", model);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            userId,
            "UpdateProfile",
            "UserProfile",
            userId ?? string.Empty,
            "Cập nhật họ tên, số điện thoại hoặc địa chỉ hồ sơ cá nhân.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật hồ sơ cá nhân.";
        return RedirectToAction(nameof(Index));
    }

    private static ProfileViewModel ToViewModel(ApplicationUser user) => new()
    {
        FullName = user.FullName,
        Email = user.Email ?? string.Empty,
        PhoneNumber = user.PhoneNumber ?? string.Empty,
        Address = user.Address ?? string.Empty,
        CreatedAt = user.CreatedAt
    };
}
