using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin + "," + RoleNames.Staff)]
public sealed class RentalSignedDocumentFilesController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _secureDocumentStorage;

    public RentalSignedDocumentFilesController(
        ApplicationDbContext dbContext,
        ISecureDocumentStorage secureDocumentStorage)
    {
        _dbContext = dbContext;
        _secureDocumentStorage = secureDocumentStorage;
    }

    [HttpGet]
    public async Task<IActionResult> Page(
        int bookingId,
        bool handover,
        int index,
        CancellationToken cancellationToken)
    {
        if (bookingId <= 0 || index < 0)
        {
            return BadRequest();
        }

        string? imagePaths;
        string marker;

        if (handover)
        {
            imagePaths = await _dbContext.VehicleHandovers
                .AsNoTracking()
                .Where(item => item.BookingId == bookingId)
                .Select(item => item.ImagePaths)
                .FirstOrDefaultAsync(cancellationToken);
            marker = HandoverSignedMarker;
        }
        else
        {
            imagePaths = await _dbContext.VehicleReturns
                .AsNoTracking()
                .Where(item => item.BookingId == bookingId)
                .Select(item => item.ImagePaths)
                .FirstOrDefaultAsync(cancellationToken);
            marker = ReturnSignedMarker;
        }

        var signedPaths = SplitPaths(imagePaths)
            .Where(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (index >= signedPaths.Length)
        {
            return NotFound();
        }

        var storedPath = signedPaths[index];
        if (!_secureDocumentStorage.TryResolve(storedPath, out var fullPath, out var contentType))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        return PhysicalFile(fullPath, contentType, enableRangeProcessing: false);
    }

    private static IReadOnlyList<string> SplitPaths(string? paths) =>
        string.IsNullOrWhiteSpace(paths)
            ? Array.Empty<string>()
            : paths.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
}
