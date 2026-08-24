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
    private const int MaximumSignedDocumentPages = 12;
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

        var signedPaths = FindSignedPaths(record.ImagePaths, HandoverSignedMarker);
        return View(new HandoverDocumentViewModel
        {
            Booking = booking,
            RecordedAt = record.HandoverAt,
            Mileage = record.Mileage,
            FuelLevel = record.FuelLevel,
            ExteriorCondition = record.ExteriorCondition,
            InteriorCondition = record.InteriorCondition,
            Accessories = record.Accessories,
            Notes = record.Notes,
            IncludedKilometers = record.IncludedKilometers,
            ExcessKmFeePerKm = record.ExcessKmFeePerKm,
            LateReturnFeeMultiplier = record.LateReturnFeeMultiplier,
            TrafficFineTerms = record.TrafficFineTerms,
            DamageCompensationTerms = record.DamageCompensationTerms,
            PenaltyPolicyAccepted = record.PenaltyPolicyAccepted,
            ImagePaths = VehiclePhotos(record.ImagePaths, HandoverSignedMarker),
            SignedDocumentPaths = signedPaths,
            SignedDocumentPath = signedPaths.FirstOrDefault()
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

        var signedPaths = FindSignedPaths(record.ImagePaths, ReturnSignedMarker);
        return View(new ReturnDocumentViewModel
        {
            Booking = booking,
            RecordedAt = record.ReturnedAt,
            Mileage = record.Mileage,
            FuelLevel = record.FuelLevel,
            ExteriorCondition = record.ExteriorCondition,
            InteriorCondition = record.InteriorCondition,
            HasDamage = record.HasDamage,
            IsLateReturn = record.IsLateReturn,
            LateMinutes = record.LateMinutes,
            LateFee = record.LateFee,
            Notes = record.Notes,
            ImagePaths = VehiclePhotos(record.ImagePaths, ReturnSignedMarker),
            SignedDocumentPaths = signedPaths,
            SignedDocumentPath = signedPaths.FirstOrDefault()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadHandoverSigned(
        int bookingId,
        List<IFormFile>? signedDocuments,
        IFormFile? signedDocument,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Handover)
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var existingSigned = FindSignedPaths(booking.Handover.ImagePaths, HandoverSignedMarker);
        if (booking.Status == BookingStatus.Completed && existingSigned.Count > 0)
        {
            TempData["ErrorMessage"] = "Hồ sơ đã hoàn tất nên bản ký không thể thay đổi.";
            return RedirectToAction("Details", "AdminTripRecords", new { id = bookingId });
        }

        var selectedSignedDocuments = CollectSignedFiles(signedDocuments, signedDocument);
        var error = await ValidateSignedDocumentsAsync(selectedSignedDocuments, cancellationToken);
        if (error is not null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId });
        }

        var newPaths = await SaveSignedDocumentsAsync(
            bookingId,
            "handovers",
            HandoverSignedMarker,
            selectedSignedDocuments,
            cancellationToken);

        var startsRental = booking.Status == BookingStatus.ReadyForPickup;
        try
        {
            ReplaceSignedPaths(booking.Handover, newPaths, HandoverSignedMarker);

            if (startsRental)
            {
                if (booking.Vehicle.Status != VehicleStatus.Available)
                {
                    DeletePhysicalFiles(newPaths);
                    TempData["ErrorMessage"] = "Xe không còn ở trạng thái sẵn sàng để giao.";
                    return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
                }

                booking.Status = BookingStatus.Rented;
                booking.Vehicle.Status = VehicleStatus.Rented;
                booking.Vehicle.CurrentMileage = booking.Handover.Mileage;

                _dbContext.Notifications.Add(new Notification
                {
                    UserId = booking.CustomerId,
                    Title = "Đã bàn giao xe",
                    Message = $"Đơn #{booking.BookingId} đã hoàn tất bàn giao và bắt đầu chuyến thuê."
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DeletePhysicalFiles(newPaths);
            throw;
        }

        DeletePhysicalFiles(existingSigned);

        await WriteAuditAsync(
            "UploadSignedHandover",
            nameof(VehicleHandover),
            bookingId,
            startsRental
                ? $"Tải {newPaths.Count} trang bản giao có chữ ký và bắt đầu chuyến thuê của đơn #{bookingId}."
                : $"Tải {newPaths.Count} trang bản ký tay biên bản giao xe của đơn #{bookingId}.",
            cancellationToken);

        TempData["SuccessMessage"] = startsRental
            ? $"Đã lưu {newPaths.Count} trang bản ký. Chuyến thuê đã bắt đầu."
            : $"Đã lưu {newPaths.Count} trang bản giao xe có chữ ký.";

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadReturnSigned(
        int bookingId,
        List<IFormFile>? signedDocuments,
        IFormFile? signedDocument,
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

        var existingSigned = FindSignedPaths(booking.VehicleReturn.ImagePaths, ReturnSignedMarker);
        if (booking.Status == BookingStatus.Completed && existingSigned.Count > 0)
        {
            TempData["ErrorMessage"] = "Hồ sơ đã hoàn tất nên bản ký không thể thay đổi.";
            return RedirectToAction("Details", "AdminTripRecords", new { id = bookingId });
        }

        var selectedSignedDocuments = CollectSignedFiles(signedDocuments, signedDocument);
        var error = await ValidateSignedDocumentsAsync(selectedSignedDocuments, cancellationToken);
        if (error is not null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToAction("Inspect", "Returns", new { bookingId });
        }

        var newPaths = await SaveSignedDocumentsAsync(
            bookingId,
            "returns",
            ReturnSignedMarker,
            selectedSignedDocuments,
            cancellationToken);

        try
        {
            ReplaceSignedPaths(booking.VehicleReturn, newPaths, ReturnSignedMarker);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DeletePhysicalFiles(newPaths);
            throw;
        }

        DeletePhysicalFiles(existingSigned);

        await WriteAuditAsync(
            "UploadSignedReturn",
            nameof(VehicleReturn),
            bookingId,
            $"Tải {newPaths.Count} trang bản ký tay biên bản trả xe của đơn #{bookingId}.",
            cancellationToken);

        TempData["SuccessMessage"] = $"Đã lưu {newPaths.Count} trang bản trả xe có chữ ký.";
        return RedirectToAction("Inspect", "Returns", new { bookingId });
    }

    private static IReadOnlyList<string> SplitPaths(string? imagePaths) =>
        string.IsNullOrWhiteSpace(imagePaths)
            ? Array.Empty<string>()
            : imagePaths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    internal static string? FindSignedPath(string? imagePaths, string marker) =>
        FindSignedPaths(imagePaths, marker).FirstOrDefault();

    internal static IReadOnlyList<string> FindSignedPaths(string? imagePaths, string marker) =>
        SplitPaths(imagePaths)
            .Where(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static IReadOnlyList<string> VehiclePhotos(string? imagePaths, string marker) =>
        SplitPaths(imagePaths)
            .Where(path => !path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void ReplaceSignedPaths(VehicleHandover record, IReadOnlyCollection<string> newPaths, string marker)
    {
        record.ImagePaths = string.Join(
            ';',
            SplitPaths(record.ImagePaths)
                .Where(path => !path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Concat(newPaths));
    }

    private static void ReplaceSignedPaths(VehicleReturn record, IReadOnlyCollection<string> newPaths, string marker)
    {
        record.ImagePaths = string.Join(
            ';',
            SplitPaths(record.ImagePaths)
                .Where(path => !path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Concat(newPaths));
    }

    private static List<IFormFile> CollectSignedFiles(
        IEnumerable<IFormFile>? files,
        IFormFile? singleFile)
    {
        var selectedFiles = files?
            .Where(file => file.Length > 0)
            .ToList() ?? new List<IFormFile>();

        if (singleFile is { Length: > 0 })
        {
            selectedFiles.Add(singleFile);
        }

        return selectedFiles;
    }

    private async Task<string?> ValidateSignedDocumentsAsync(
        IReadOnlyCollection<IFormFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return "Vui lòng chọn ít nhất một ảnh chụp/scan biên bản đã ký.";
        }

        if (files.Count > MaximumSignedDocumentPages)
        {
            return $"Chỉ được tải tối đa {MaximumSignedDocumentPages} ảnh chụp/scan biên bản đã ký trong một lần.";
        }

        var pageNumber = 1;
        foreach (var file in files)
        {
            var validationError = await ImageFileValidator.ValidateAsync(
                file,
                MaximumSignedDocumentBytes,
                cancellationToken);

            if (validationError is not null)
            {
                return $"Bản ký trang {pageNumber}: {validationError}";
            }

            pageNumber++;
        }

        return null;
    }

    private async Task<List<string>> SaveSignedDocumentsAsync(
        int bookingId,
        string documentFolder,
        string marker,
        IEnumerable<IFormFile> files,
        CancellationToken cancellationToken)
    {
        var selectedFiles = files
            .Where(file => file.Length > 0)
            .ToList();

        var paths = new List<string>();
        if (selectedFiles.Count == 0)
        {
            return paths;
        }

        var relativeFolder = $"uploads/{documentFolder}/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        try
        {
            foreach (var file in selectedFiles)
            {
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                var fileName = $"{marker}{Guid.NewGuid():N}{extension}";
                var fullPath = Path.Combine(folder, fileName);

                await using var stream = System.IO.File.Create(fullPath);
                await file.CopyToAsync(stream, cancellationToken);

                paths.Add($"/{relativeFolder}/{fileName}");
            }
        }
        catch
        {
            DeletePhysicalFiles(paths);
            throw;
        }

        return paths;
    }

    private void DeletePhysicalFiles(IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            DeletePhysicalFile(relativePath);
        }
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
