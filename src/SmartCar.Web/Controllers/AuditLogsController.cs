using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AuditLogsController : Controller
{
    private readonly IAuditService _auditService;

    public AuditLogsController(IAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        ViewBag.Take = Math.Clamp(take, 1, 1000);
        return View(await _auditService.GetRecentAsync(ViewBag.Take, cancellationToken));
    }
}
