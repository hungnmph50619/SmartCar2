using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class ReturnHandoverPreviewController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private readonly ApplicationDbContext _dbContext;

    public ReturnHandoverPreviewController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int bookingId, CancellationToken cancellationToken)
    {
        var handover = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (handover is null)
        {
            return NotFound();
        }

        ViewBag.ImagePaths = string.IsNullOrWhiteSpace(handover.ImagePaths)
            ? Array.Empty<string>()
            : handover.ImagePaths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !path.Contains(HandoverSignedMarker, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        return View(handover);
    }
}
