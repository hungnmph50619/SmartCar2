using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
[Route("StaffWorkflowDiagnostics")]
public sealed class StaffWorkflowDiagnosticsController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly ApplicationDbContext _dbContext;

    public StaffWorkflowDiagnosticsController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("Inspection")]
    public async Task<IActionResult> Inspection(int bookingId, CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            return Json(new
            {
                bookingId,
                canInspect = false,
                status = booking.Status.ToString(),
                blockers = new[] { "Đơn không còn ở trạng thái chờ kiểm tra xe trả." }
            });
        }

        var handover = booking.Handover;
        var vehicleReturn = booking.VehicleReturn;
        var handoverSignedFileExists = HasSignedCopy(handover?.ImagePaths, HandoverSignedMarker);
        var returnSignedFileExists = HasSignedCopy(vehicleReturn?.ImagePaths, ReturnSignedMarker);

        var blockers = new List<string>();
        if (handover is null)
        {
            blockers.Add("Thiếu biên bản giao xe.");
        }
        else
        {
            if (!handover.CustomerIdentityVerified)
                blockers.Add("Biên bản giao chưa xác minh đúng người nhận xe.");
            if (!handoverSignedFileExists)
                blockers.Add("Biên bản giao chưa có ảnh/scan bản ký.");
            else if (!handover.SignedDocumentVerified)
                blockers.Add("Bản ký giao xe đã tải nhưng Staff chưa xác minh.");
        }

        if (vehicleReturn is null)
        {
            blockers.Add("Thiếu biên bản trả xe.");
        }
        else
        {
            if (!vehicleReturn.CustomerIdentityVerified)
                blockers.Add("Biên bản trả chưa xác minh đúng người trả xe.");
            if (!returnSignedFileExists)
                blockers.Add("Biên bản trả chưa có ảnh/scan bản ký.");
            else if (!vehicleReturn.SignedDocumentVerified)
                blockers.Add("Bản ký trả xe đã tải nhưng Staff chưa xác minh.");
        }

        return Json(new
        {
            bookingId,
            canInspect = blockers.Count == 0,
            blockers,
            checks = new
            {
                handoverIdentity = handover?.CustomerIdentityVerified == true,
                handoverSignedFile = handoverSignedFileExists,
                handoverSignedVerified = handover?.SignedDocumentVerified == true,
                returnIdentity = vehicleReturn?.CustomerIdentityVerified == true,
                returnSignedFile = returnSignedFileExists,
                returnSignedVerified = vehicleReturn?.SignedDocumentVerified == true
            },
            inspectUrl = Url.Action("Inspect", "Returns", new { bookingId })
        });
    }

    private static bool HasSignedCopy(string? paths, string marker) =>
        !string.IsNullOrWhiteSpace(paths) &&
        paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
