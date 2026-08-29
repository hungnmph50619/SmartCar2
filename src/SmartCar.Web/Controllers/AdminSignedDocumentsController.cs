using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminSignedDocumentsController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";
    private readonly ApplicationDbContext _dbContext;

    public AdminSignedDocumentsController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Handover(int bookingId, CancellationToken cancellationToken)
    {
        var paths = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => item.ImagePaths)
            .FirstOrDefaultAsync(cancellationToken);

        var signedPath = FindSignedPath(paths, HandoverSignedMarker);
        if (signedPath is null)
        {
            TempData["ErrorMessage"] = "Biên bản giao xe chưa có bản ký để xem.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return Redirect(signedPath);
    }

    [HttpGet]
    public async Task<IActionResult> Return(int bookingId, CancellationToken cancellationToken)
    {
        var paths = await _dbContext.VehicleReturns
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => item.ImagePaths)
            .FirstOrDefaultAsync(cancellationToken);

        var signedPath = FindSignedPath(paths, ReturnSignedMarker);
        if (signedPath is null)
        {
            TempData["ErrorMessage"] = "Biên bản trả xe chưa có bản ký để xem.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return Redirect(signedPath);
    }

    private static string? FindSignedPath(string? paths, string marker) =>
        string.IsNullOrWhiteSpace(paths)
            ? null
            : paths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
