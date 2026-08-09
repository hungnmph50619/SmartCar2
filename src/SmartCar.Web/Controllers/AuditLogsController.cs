using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AuditLogsController : Controller
{
    private static readonly TimeZoneInfo VietnamTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "SmartCar-Vietnam",
        TimeSpan.FromHours(7),
        "Việt Nam",
        "Việt Nam");

    private readonly IAuditService _auditService;

    public AuditLogsController(IAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? search,
        string? userId,
        string? auditAction,
        string? entityName,
        DateTime? fromDate,
        DateTime? toDate,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var normalizedFrom = fromDate?.Date;
        var normalizedTo = toDate?.Date;

        if (normalizedFrom.HasValue && normalizedTo.HasValue && normalizedFrom > normalizedTo)
        {
            (normalizedFrom, normalizedTo) = (normalizedTo, normalizedFrom);
        }

        var result = await _auditService.SearchAsync(
            new AuditLogQuery(
                Search: search,
                UserId: userId,
                Action: auditAction,
                EntityName: entityName,
                FromDate: ToUtc(normalizedFrom),
                ToDate: ToUtc(normalizedTo?.AddDays(1)),
                Page: page,
                PageSize: pageSize),
            cancellationToken);

        return View(new AuditLogIndexViewModel
        {
            Search = search?.Trim(),
            UserId = userId,
            Action = auditAction,
            EntityName = entityName,
            FromDate = normalizedFrom,
            ToDate = normalizedTo,
            Result = result
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        long id,
        CancellationToken cancellationToken = default)
    {
        var auditLog = await _auditService.GetByIdAsync(id, cancellationToken);
        return auditLog is null ? NotFound() : View(auditLog);
    }

    private static DateTime? ToUtc(DateTime? vietnamDateTime)
    {
        if (!vietnamDateTime.HasValue)
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(vietnamDateTime.Value, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, VietnamTimeZone);
    }
}
