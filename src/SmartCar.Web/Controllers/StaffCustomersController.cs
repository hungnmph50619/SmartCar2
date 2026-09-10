using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Accounts;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff + "," + RoleNames.Admin)]
public sealed class StaffCustomersController : Controller
{
    private readonly IAccountService _accountService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public StaffCustomersController(
        IAccountService accountService,
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _accountService = accountService;
        _dbContext = dbContext;
        _auditService = auditService;
    }

    [HttpGet]
    public IActionResult Create() => View(new StaffCreateCustomerViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        StaffCreateCustomerViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _accountService.RegisterCustomerAsync(
            new RegisterCustomerRequest(
                model.FullName,
                model.PhoneNumber,
                model.Email,
                model.Password),
            cancellationToken);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        var normalizedEmail = model.Email.Trim().ToLowerInvariant();
        var customer = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Email != null && user.Email.ToLower() == normalizedEmail)
            .Select(user => new { user.Id, user.FullName, user.Email })
            .FirstOrDefaultAsync(cancellationToken);

        await _auditService.WriteAsync(
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            "StaffCreateCustomer",
            "UserAccount",
            customer?.Id ?? normalizedEmail,
            $"Tạo tài khoản Customer tại quầy cho {customer?.FullName ?? model.FullName.Trim()} ({normalizedEmail}). Không tự duyệt KYC.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã tạo tài khoản khách hàng. Bước tiếp theo: khách phải hoàn tất CCCD + GPLX và được Admin xác minh trước khi tạo đơn thuê.";

        return RedirectToAction("CounterRental", "Staff", new { customerId = customer?.Id });
    }
}
