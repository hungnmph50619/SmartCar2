using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin + "," + RoleNames.Staff + "," + RoleNames.Customer)]
public sealed class ExtensionEvidenceFilesController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _secureDocumentStorage;

    public ExtensionEvidenceFilesController(
        ApplicationDbContext dbContext,
        ISecureDocumentStorage secureDocumentStorage)
    {
        _dbContext = dbContext;
        _secureDocumentStorage = secureDocumentStorage;
    }

    [HttpGet]
    public async Task<IActionResult> Image(
        int extensionId,
        int index,
        CancellationToken cancellationToken)
    {
        if (extensionId <= 0 || index < 0)
        {
            return BadRequest();
        }

        var record = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Where(extension =>
                extension.BookingExtensionId == extensionId)
            .Select(extension => new
            {
                extension.Booking.CustomerId,
                extension.CustomerNote
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (record is null)
        {
            return NotFound();
        }

        if (User.IsInRole(RoleNames.Customer))
        {
            var currentUserId = User.FindFirstValue(
                ClaimTypes.NameIdentifier);

            if (!string.Equals(
                    currentUserId,
                    record.CustomerId,
                    StringComparison.Ordinal))
            {
                return Forbid();
            }
        }

        var paths = ExtensionEvidencePathParser.ExtractImagePaths(
            record.CustomerNote);

        if (index >= paths.Count)
        {
            return NotFound();
        }

        if (!_secureDocumentStorage.TryResolve(
                paths[index],
                out var fullPath,
                out var contentType))
        {
            return NotFound();
        }

        Response.Headers.CacheControl =
            "private, no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        return PhysicalFile(
            fullPath,
            contentType,
            enableRangeProcessing: false);
    }
}
