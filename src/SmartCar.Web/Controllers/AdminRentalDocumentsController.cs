using System.Data;
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

[Authorize(Roles = RoleNames.Staff)]
public sealed class AdminRentalDocumentsController : Controller
{
    private const long MaximumSignedDocumentBytes = 8 * 1024 * 1024;
    private const int MaximumSignedDocumentPages = 12;
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly IBookingService _bookingService;
    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _secureDocumentStorage;
    private readonly IAuditService _auditService;

    public AdminRentalDocumentsController(
        IBookingService bookingService,
        ApplicationDbContext dbContext,
        ISecureDocumentStorage secureDocumentStorage,
        IAuditService auditService)
    {
        _bookingService = bookingService;
        _dbContext = dbContext;
        _secureDocumentStorage = secureDocumentStorage;
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
            return RedirectToBookingDetails(bookingId);
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
            return RedirectToBookingDetails(bookingId);
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
            Accessories = record.AccessoryStatus,
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
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe.";
            return RedirectToBookingDetails(bookingId);
        }

        var existingSigned = FindSignedPaths(
    booking.Handover.ImagePaths,
    HandoverSignedMarker);

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] =
                "Chỉ được tải hoặc thay bản ký giao xe khi đơn đang ở trạng thái sẵn sàng giao xe.";

            return RedirectToBookingDetails(bookingId);
        }

        if (booking.Handover.SignedDocumentVerified)
        {
            TempData["ErrorMessage"] =
                "Bản ký giao xe đã được nhân viên xác minh nên không thể thay đổi.";

            return RedirectToBookingDetails(bookingId);
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

        try
        {
            ReplaceSignedPaths(booking.Handover, newPaths, HandoverSignedMarker);
            booking.Handover.SignedDocumentVerified = false;
            booking.Handover.SignedDocumentVerifiedByStaffId = null;
            booking.Handover.SignedDocumentVerifiedAt = null;
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
            $"Tải {newPaths.Count} trang bản ký tay biên bản giao xe của đơn #{bookingId}; chờ nhân viên xác minh nội dung/chữ ký.",
            cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã lưu {newPaths.Count} trang bản ký. Hãy mở kiểm tra và xác nhận hợp lệ trước khi bắt đầu chuyến.";

        return RedirectToBookingDetails(bookingId);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadReturnSigned(
        int bookingId,
        List<IFormFile>? signedDocuments,
        IFormFile? signedDocument,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.VehicleReturn is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản trả xe.";
            return RedirectToBookingDetails(bookingId);
        }

        var existingSigned = FindSignedPaths(
    booking.VehicleReturn.ImagePaths,
    ReturnSignedMarker);

        if (booking.Status != BookingStatus.PendingInspection)
        {
            TempData["ErrorMessage"] =
                "Chỉ được tải hoặc thay bản ký trả xe khi đơn đang ở bước chờ kiểm tra.";

            return RedirectToBookingDetails(bookingId);
        }

        if (booking.VehicleReturn.SignedDocumentVerified)
        {
            TempData["ErrorMessage"] =
                "Bản ký trả xe đã được nhân viên xác minh nên không thể thay đổi.";

            return RedirectToBookingDetails(bookingId);
        }

        var selectedSignedDocuments = CollectSignedFiles(signedDocuments, signedDocument);
        var error = await ValidateSignedDocumentsAsync(selectedSignedDocuments, cancellationToken);
        if (error is not null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToBookingDetails(bookingId);
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
            booking.VehicleReturn.SignedDocumentVerified = false;
            booking.VehicleReturn.SignedDocumentVerifiedByStaffId = null;
            booking.VehicleReturn.SignedDocumentVerifiedAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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

        TempData["SuccessMessage"] =
            $"Đã lưu {newPaths.Count} trang bản trả xe. Nhân viên cần xác minh chữ ký trước khi quyết toán.";
        return RedirectToBookingDetails(bookingId);
    }

    private IActionResult RedirectToBookingDetails(int bookingId) =>
        User.IsInRole(RoleNames.Staff)
            ? RedirectToAction("Details", "Staff", new { id = bookingId })
            : RedirectToAction("Details", "AdminBookings", new { id = bookingId });

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

        var secureOwnerId = $"{marker}{documentFolder}-{bookingId}";

        try
        {
            foreach (var file in selectedFiles)
            {
                paths.Add(await _secureDocumentStorage.SaveAsync(
                    file,
                    secureOwnerId,
                    cancellationToken));
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

    private void DeletePhysicalFile(string? relativePath) =>
        _secureDocumentStorage.Delete(relativePath);

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