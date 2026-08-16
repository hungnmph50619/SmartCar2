using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminRentalDocumentsController : Controller
{
    private const long MaximumSignedDocumentBytes = 8 * 1024 * 1024;
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly IBookingService _bookingService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly IAuditService _auditService;

    public AdminRentalDocumentsController(
        IBookingService bookingService,
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        IAuditService auditService)
    {
        _bookingService = bookingService;
        _dbContext = dbContext;
        _environment = environment;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> HandoverPrint(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var record = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (record is null)
        {
            TempData["ErrorMessage"] = "Đơn chưa có biên bản giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View(new HandoverDocumentViewModel
        {
            Booking = booking,
            RecordedAt = record.HandoverAt,
            Mileage = record.Mileage,
            FuelLevel = record.FuelLevel,
            Notes = record.Notes,
            IncludedKilometers = record.IncludedKilometers,
            ExcessKmFeePerKm = record.ExcessKmFeePerKm,
            LateReturnFeeMultiplier = record.LateReturnFeeMultiplier,
            TrafficFineTerms = record.TrafficFineTerms,
            DamageCompensationTerms = record.DamageCompensationTerms,
            PenaltyPolicyAccepted = record.PenaltyPolicyAccepted,
            ImagePaths = VehiclePhotos(record.ImagePaths, HandoverSignedMarker),
            SignedDocumentPath = FindSignedPath(record.ImagePaths, HandoverSignedMarker)
        });
    }

    [HttpGet]
    public async Task<IActionResult> ReturnPrint(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var record = await _dbContext.VehicleReturns
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (record is null)
        {
            TempData["ErrorMessage"] = "Đơn chưa có biên bản trả xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View(new ReturnDocumentViewModel
        {
            Booking = booking,
            RecordedAt = record.ReturnedAt,
            Mileage = record.Mileage,
            FuelLevel = record.FuelLevel,
            HasDamage = record.HasDamage,
            IsLateReturn = record.IsLateReturn,
            LateMinutes = record.LateMinutes,
            LateFee = record.LateFee,
            Notes = record.Notes,
            ImagePaths = VehiclePhotos(record.ImagePaths, ReturnSignedMarker),
            SignedDocumentPath = FindSignedPath(record.ImagePaths, ReturnSignedMarker)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadHandoverSigned(
        int bookingId,
        IFormFile signedDocument,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var existingSigned = FindSignedPath(booking.Handover.ImagePaths, HandoverSignedMarker);
        if (booking.Status == BookingStatus.Completed && existingSigned is not null)
        {
            TempData["ErrorMessage"] = "Hồ sơ đã hoàn tất nên bản ký không thể thay đổi.";
            return RedirectToAction("Details", "AdminTripRecords", new { id = bookingId });
        }

        var error = await ValidateSignedDocumentAsync(signedDocument, cancellationToken);
        if (error is not null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var newPath = await SaveSignedDocumentAsync(
            bookingId,
            "handovers",
            HandoverSignedMarker,
            signedDocument,
            cancellationToken);

        ReplaceSignedPath(booking.Handover, newPath, HandoverSignedMarker);
        await _dbContext.SaveChangesAsync(cancellationToken);
        DeletePhysicalFile(existingSigned);

        await WriteAuditAsync(
            "UploadSignedHandover",
            nameof(VehicleHandover),
            bookingId,
            $"Tải bản ký tay biên bản giao xe của đơn #{bookingId}.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã lưu bản giao xe có chữ ký.";
        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadReturnSigned(
        int bookingId,
        IFormFile signedDocument,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.VehicleReturn is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản trả xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var existingSigned = FindSignedPath(booking.VehicleReturn.ImagePaths, ReturnSignedMarker);
        if (booking.Status == BookingStatus.Completed && existingSigned is not null)
        {
            TempData["ErrorMessage"] = "Hồ sơ đã hoàn tất nên bản ký không thể thay đổi.";
            return RedirectToAction("Details", "AdminTripRecords", new { id = bookingId });
        }

        var error = await ValidateSignedDocumentAsync(signedDocument, cancellationToken);
        if (error is not null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToAction("Inspect", "Returns", new { bookingId });
        }

        var newPath = await SaveSignedDocumentAsync(
            bookingId,
            "returns",
            ReturnSignedMarker,
            signedDocument,
            cancellationToken);

        ReplaceSignedPath(booking.VehicleReturn, newPath, ReturnSignedMarker);
        await _dbContext.SaveChangesAsync(cancellationToken);
        DeletePhysicalFile(existingSigned);

        await WriteAuditAsync(
            "UploadSignedReturn",
            nameof(VehicleReturn),
            bookingId,
            $"Tải bản ký tay biên bản trả xe của đơn #{bookingId}.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã lưu bản trả xe có chữ ký.";
        return RedirectToAction("Inspect", "Returns", new { bookingId });
    }

    private static IReadOnlyList<string> SplitPaths(string? imagePaths) =>
        string.IsNullOrWhiteSpace(imagePaths)
            ? Array.Empty<string>()
            : imagePaths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    internal static string? FindSignedPath(string? imagePaths, string marker) =>
        SplitPaths(imagePaths)
            .FirstOrDefault(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> VehiclePhotos(string? imagePaths, string marker) =>
        SplitPaths(imagePaths)
            .Where(path => !path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void ReplaceSignedPath(VehicleHandover record, string newPath, string marker)
    {
        record.ImagePaths = string.Join(
            ';',
            SplitPaths(record.ImagePaths)
                .Where(path => !path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Append(newPath));
    }

    private static void ReplaceSignedPath(VehicleReturn record, string newPath, string marker)
    {
        record.ImagePaths = string.Join(
            ';',
            SplitPaths(record.ImagePaths)
                .Where(path => !path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Append(newPath));
    }

    private async Task<string?> ValidateSignedDocumentAsync(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return "Vui lòng chọn ảnh chụp/scan biên bản đã ký.";
        }

        var validationError = await ImageFileValidator.ValidateAsync(
            file,
            MaximumSignedDocumentBytes,
            cancellationToken);

        return validationError is null
            ? null
            : $"Bản ký: {validationError}";
    }

    private async Task<string> SaveSignedDocumentAsync(
        int bookingId,
        string documentFolder,
        string marker,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/{documentFolder}/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName = $"{marker}{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(folder, fileName);

        await using var stream = System.IO.File.Create(fullPath);
        await file.CopyToAsync(stream, cancellationToken);

        return $"/{relativeFolder}/{fileName}";
    }

    private void DeletePhysicalFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var fullPath = Path.Combine(
            _environment.WebRootPath,
            relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }
    }

    private Task WriteAuditAsync(
        string action,
        string entityName,
        int bookingId,
        string description,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return _auditService.WriteAsync(
            adminId,
            action,
            entityName,
            bookingId.ToString(),
            description,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }
}
