using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class AdminSignedDocumentsController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly ApplicationDbContext _dbContext;

    public AdminSignedDocumentsController(
        ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Handover(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var record = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new
            {
                item.ImagePaths,
                item.SignedDocumentVerified,
                item.SignedDocumentVerifiedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (record is null)
        {
            return NotFound();
        }

        var paths = FindSignedPaths(
            record.ImagePaths,
            HandoverSignedMarker);

        if (paths.Count == 0)
        {
            TempData["ErrorMessage"] =
                "Biên bản giao xe chưa có bản ký để xem.";

            return RedirectToBookingDetails(bookingId);
        }

        return View(
            "Pages",
            new SignedDocumentPagesViewModel
            {
                BookingId = bookingId,
                DocumentTitle = "Bản ký biên bản giao xe",
                IsHandover = true,
                IsVerified = record.SignedDocumentVerified,
                VerifiedAt = record.SignedDocumentVerifiedAt,
                Paths = paths
            });
    }

    [HttpGet]
    public async Task<IActionResult> Return(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var record = await _dbContext.VehicleReturns
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new
            {
                item.ImagePaths,
                item.SignedDocumentVerified,
                item.SignedDocumentVerifiedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (record is null)
        {
            return NotFound();
        }

        var paths = FindSignedPaths(
            record.ImagePaths,
            ReturnSignedMarker);

        if (paths.Count == 0)
        {
            TempData["ErrorMessage"] =
                "Biên bản trả xe chưa có bản ký để xem.";

            return RedirectToBookingDetails(bookingId);
        }

        return View(
            "Pages",
            new SignedDocumentPagesViewModel
            {
                BookingId = bookingId,
                DocumentTitle = "Bản ký biên bản trả xe",
                IsHandover = false,
                IsVerified = record.SignedDocumentVerified,
                VerifiedAt = record.SignedDocumentVerifiedAt,
                Paths = paths
            });
    }

    private IActionResult RedirectToBookingDetails(
        int bookingId) =>
        RedirectToAction(
            "Details",
            "Staff",
            new { id = bookingId });

    private static IReadOnlyList<string> FindSignedPaths(
        string? paths,
        string marker) =>
        string.IsNullOrWhiteSpace(paths)
            ? Array.Empty<string>()
            : paths
                .Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Where(path =>
                    path.Contains(
                        marker,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
}