using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
[Route("StaffCounterIdentityEvidence")]
public sealed class StaffCounterIdentityEvidenceController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _secureStorage;
    private readonly IAuditService _auditService;

    public StaffCounterIdentityEvidenceController(
        ApplicationDbContext dbContext,
        ISecureDocumentStorage secureStorage,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _secureStorage = secureStorage;
        _auditService = auditService;
    }

    [HttpGet("HandoverStatus")]
    public async Task<IActionResult> HandoverStatus(int bookingId, CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new { item.BookingId, item.CustomerId, item.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        var sessions = await GetCurrentSessionsAsync(
            booking.BookingId,
            booking.CustomerId,
            staffId,
            isReturn: false,
            cancellationToken);

        return Json(new
        {
            bookingId,
            allowed = booking.Status == BookingStatus.ReadyForPickup,
            frontReady = sessions.Front is not null,
            backReady = sessions.Back is not null,
            ready = sessions.Front is not null && sessions.Back is not null,
            frontUrl = sessions.Front is null
                ? null
                : Url.Action(nameof(CurrentHandoverCitizen), new { bookingId, side = "front" }),
            backUrl = sessions.Back is null
                ? null
                : Url.Action(nameof(CurrentHandoverCitizen), new { bookingId, side = "back" })
        });
    }

    [HttpGet("ReturnStatus")]
    public async Task<IActionResult> ReturnStatus(int bookingId, CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId)) return Challenge();

        var booking = await _dbContext.Bookings.AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new { item.BookingId, item.CustomerId, item.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null) return NotFound();

        var sessions = await GetCurrentSessionsAsync(
            booking.BookingId, booking.CustomerId, staffId, isReturn: true, cancellationToken);
        return Json(new
        {
            bookingId,
            allowed = booking.Status == BookingStatus.Rented,
            frontReady = sessions.Front is not null,
            backReady = sessions.Back is not null,
            ready = sessions.Front is not null && sessions.Back is not null,
            frontUrl = sessions.Front is null ? null : Url.Action(nameof(CurrentReturnCitizen), new { bookingId, side = "front" }),
            backUrl = sessions.Back is null ? null : Url.Action(nameof(CurrentReturnCitizen), new { bookingId, side = "back" })
        });
    }

    [HttpGet("KycCitizen")]
    public async Task<IActionResult> KycCitizen(
        int bookingId,
        string side,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new { item.CustomerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var documentType = string.Equals(side, "back", StringComparison.OrdinalIgnoreCase)
            ? DocumentTypes.CitizenIdBack
            : string.Equals(side, "front", StringComparison.OrdinalIgnoreCase)
                ? DocumentTypes.CitizenId
                : null;
        if (documentType is null)
        {
            return BadRequest();
        }

        var storedPath = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == booking.CustomerId &&
                document.DocumentType == documentType &&
                document.Status == DocumentStatus.Verified)
            .OrderByDescending(document => document.VerifiedAt)
            .Select(document => document.ImagePath)
            .FirstOrDefaultAsync(cancellationToken);

        return SecureImage(storedPath);
    }

    [HttpGet("CurrentHandoverCitizen")]
    public async Task<IActionResult> CurrentHandoverCitizen(
        int bookingId,
        string side,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new { item.CustomerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var purpose = string.Equals(side, "back", StringComparison.OrdinalIgnoreCase)
            ? IdentityCapturePurposes.HandoverCitizenBack
            : string.Equals(side, "front", StringComparison.OrdinalIgnoreCase)
                ? IdentityCapturePurposes.HandoverCitizenFront
                : null;
        if (purpose is null)
        {
            return BadRequest();
        }

        var now = DateTime.UtcNow;
        var storedPath = await _dbContext.Set<IdentityCaptureSession>()
            .AsNoTracking()
            .Where(item =>
                item.BookingId == bookingId &&
                item.TargetCustomerId == booking.CustomerId &&
                item.CreatedByUserId == staffId &&
                item.Purpose == purpose &&
                item.CompletedAt.HasValue &&
                !item.ConsumedAt.HasValue &&
                item.ExpiresAt > now &&
                item.ImagePath != null)
            .OrderByDescending(item => item.CompletedAt)
            .Select(item => item.ImagePath)
            .FirstOrDefaultAsync(cancellationToken);

        return SecureImage(storedPath);
    }

    [HttpGet("HandoverCitizen")]
    public async Task<IActionResult> HandoverCitizen(
        int bookingId, string side, CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings.AsNoTracking()
            .Where(item => item.BookingId == bookingId && item.Handover != null)
            .Select(item => new { item.CustomerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null) return NotFound();

        var purpose = string.Equals(side, "front", StringComparison.OrdinalIgnoreCase)
            ? IdentityCapturePurposes.HandoverCitizenFront
            : string.Equals(side, "back", StringComparison.OrdinalIgnoreCase)
                ? IdentityCapturePurposes.HandoverCitizenBack : null;
        if (purpose is null) return BadRequest();

        var storedPath = await _dbContext.Set<IdentityCaptureSession>().AsNoTracking()
            .Where(item => item.BookingId == bookingId &&
                item.TargetCustomerId == booking.CustomerId &&
                item.Purpose == purpose && item.ConsumedAt.HasValue && item.ImagePath != null)
            .OrderByDescending(item => item.ConsumedAt)
            .Select(item => item.ImagePath)
            .FirstOrDefaultAsync(cancellationToken);
        return SecureImage(storedPath);
    }

    [HttpGet("CurrentReturnCitizen")]
    public async Task<IActionResult> CurrentReturnCitizen(
        int bookingId, string side, CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId)) return Challenge();

        var booking = await _dbContext.Bookings.AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new { item.CustomerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null) return NotFound();

        var purpose = string.Equals(side, "front", StringComparison.OrdinalIgnoreCase)
            ? IdentityCapturePurposes.ReturnCitizenFront
            : string.Equals(side, "back", StringComparison.OrdinalIgnoreCase)
                ? IdentityCapturePurposes.ReturnCitizenBack : null;
        if (purpose is null) return BadRequest();

        var storedPath = await _dbContext.Set<IdentityCaptureSession>().AsNoTracking()
            .Where(item => item.BookingId == bookingId &&
                item.TargetCustomerId == booking.CustomerId &&
                item.CreatedByUserId == staffId && item.Purpose == purpose &&
                item.CompletedAt.HasValue && !item.ConsumedAt.HasValue &&
                item.ExpiresAt > DateTime.UtcNow && item.ImagePath != null)
            .OrderByDescending(item => item.CompletedAt)
            .Select(item => item.ImagePath)
            .FirstOrDefaultAsync(cancellationToken);
        return SecureImage(storedPath);
    }

    [HttpPost("UploadHandover")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> UploadHandover(
        int bookingId,
        IFormFile? citizenFront,
        IFormFile? citizenBack,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup || booking.Handover is not null)
        {
            return BadRequest(new
            {
                error = "Chỉ được chụp CCCD đối chiếu khi đơn đang sẵn sàng bàn giao và chưa có biên bản giao xe."
            });
        }

        if (citizenFront is null || citizenFront.Length <= 0 || citizenBack is null || citizenBack.Length <= 0)
        {
            return BadRequest(new { error = "Cần đủ ảnh CCCD mặt trước và mặt sau của khách đang có mặt tại quầy." });
        }

        var frontError = await ImageFileValidator.ValidateAsync(
            citizenFront,
            IdentityCapturePolicy.MaximumCounterDocumentImageBytes,
            cancellationToken);
        if (frontError is not null)
        {
            return BadRequest(new { error = $"CCCD mặt trước: {frontError}" });
        }

        var backError = await ImageFileValidator.ValidateAsync(
            citizenBack,
            IdentityCapturePolicy.MaximumCounterDocumentImageBytes,
            cancellationToken);
        if (backError is not null)
        {
            return BadRequest(new { error = $"CCCD mặt sau: {backError}" });
        }

        if (await ImageFileValidator.HaveSameContentAsync(citizenFront, citizenBack, cancellationToken))
        {
            return BadRequest(new { error = "CCCD mặt trước và mặt sau không được dùng cùng một ảnh." });
        }

        var ownerKey = $"counter-id-booking-{booking.BookingId}-{booking.CustomerId}";
        string? frontPath = null;
        string? backPath = null;
        var persisted = false;

        try
        {
            frontPath = await _secureStorage.SaveAsync(citizenFront, ownerKey, cancellationToken);
            backPath = await _secureStorage.SaveAsync(citizenBack, ownerKey, cancellationToken);

            var previous = await _dbContext.Set<IdentityCaptureSession>()
                .Where(item =>
                    item.BookingId == booking.BookingId &&
                    item.TargetCustomerId == booking.CustomerId &&
                    item.CreatedByUserId == staffId &&
                    !item.ConsumedAt.HasValue &&
                    (item.Purpose == IdentityCapturePurposes.HandoverCitizenFront ||
                     item.Purpose == IdentityCapturePurposes.HandoverCitizenBack))
                .ToListAsync(cancellationToken);
            var oldPaths = previous
                .Select(item => item.ImagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _dbContext.RemoveRange(previous);

            var now = DateTime.UtcNow;
            _dbContext.Set<IdentityCaptureSession>().AddRange(
                BuildSession(
                    IdentityCapturePurposes.HandoverCitizenFront,
                    booking.BookingId,
                    booking.CustomerId,
                    staffId,
                    frontPath,
                    now),
                BuildSession(
                    IdentityCapturePurposes.HandoverCitizenBack,
                    booking.BookingId,
                    booking.CustomerId,
                    staffId,
                    backPath,
                    now));

            await _dbContext.SaveChangesAsync(cancellationToken);
            persisted = true;

            foreach (var oldPath in oldPaths)
            {
                _secureStorage.Delete(oldPath);
            }

            await _auditService.WriteAsync(
                staffId,
                "CaptureHandoverCitizenIdEvidence",
                nameof(Booking),
                booking.BookingId.ToString(),
                "Nhân viên chụp/lưu CCCD mặt trước và mặt sau của khách đang có mặt tại quầy để đối chiếu với hồ sơ KYC trước khi bàn giao xe.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            return Json(new
            {
                saved = true,
                frontReady = true,
                backReady = true,
                frontUrl = Url.Action(nameof(CurrentHandoverCitizen), new { bookingId, side = "front" }),
                backUrl = Url.Action(nameof(CurrentHandoverCitizen), new { bookingId, side = "back" })
            });
        }
        catch
        {
            if (!persisted)
            {
                if (frontPath is not null) _secureStorage.Delete(frontPath);
                if (backPath is not null) _secureStorage.Delete(backPath);
            }
            throw;
        }
    }

    [HttpPost("UploadReturn")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> UploadReturn(
        int bookingId,
        IFormFile? citizenFront,
        IFormFile? citizenBack,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId)) return Challenge();

        var booking = await _dbContext.Bookings.AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new { item.BookingId, item.CustomerId, item.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null) return NotFound();
        if (booking.Status != BookingStatus.Rented)
            return BadRequest(new { error = "Chỉ được chụp CCCD lúc xe đang được thuê và chưa lập biên bản trả." });

        var verifiedNumber = await _dbContext.CustomerDocuments.AsNoTracking()
            .Where(document => document.CustomerId == booking.CustomerId &&
                document.DocumentType == DocumentTypes.CitizenId &&
                document.Status == DocumentStatus.Verified)
            .OrderByDescending(document => document.VerifiedAt)
            .Select(document => document.DocumentNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(verifiedNumber))
        {
            return BadRequest(new { error = "Không có CCCD đã xác minh của khách đứng tên đơn. Dừng nhận xe trả và báo quản lý." });
        }

        if (citizenFront is null || citizenFront.Length <= 0 ||
            citizenBack is null || citizenBack.Length <= 0)
            return BadRequest(new { error = "Cần chụp đủ ảnh CCCD mặt trước và mặt sau của người đang trả xe." });

        var frontError = await ImageFileValidator.ValidateAsync(
            citizenFront, IdentityCapturePolicy.MaximumCounterDocumentImageBytes, cancellationToken);
        if (frontError is not null) return BadRequest(new { error = $"CCCD mặt trước: {frontError}" });
        var backError = await ImageFileValidator.ValidateAsync(
            citizenBack, IdentityCapturePolicy.MaximumCounterDocumentImageBytes, cancellationToken);
        if (backError is not null) return BadRequest(new { error = $"CCCD mặt sau: {backError}" });
        if (await ImageFileValidator.HaveSameContentAsync(citizenFront, citizenBack, cancellationToken))
            return BadRequest(new { error = "CCCD mặt trước và mặt sau không được dùng cùng một ảnh." });

        var ownerKey = $"counter-id-booking-{booking.BookingId}-{booking.CustomerId}";
        string? frontPath = null;
        string? backPath = null;
        var persisted = false;
        try
        {
            frontPath = await _secureStorage.SaveAsync(citizenFront, ownerKey, cancellationToken);
            backPath = await _secureStorage.SaveAsync(citizenBack, ownerKey, cancellationToken);
            var previous = await _dbContext.Set<IdentityCaptureSession>()
                .Where(item => item.BookingId == booking.BookingId &&
                    item.TargetCustomerId == booking.CustomerId &&
                    item.CreatedByUserId == staffId && !item.ConsumedAt.HasValue &&
                    (item.Purpose == IdentityCapturePurposes.ReturnCitizenFront ||
                     item.Purpose == IdentityCapturePurposes.ReturnCitizenBack))
                .ToListAsync(cancellationToken);
            var oldPaths = previous.Select(item => item.ImagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _dbContext.RemoveRange(previous);

            var now = DateTime.UtcNow;
            _dbContext.Set<IdentityCaptureSession>().AddRange(
                BuildSession(IdentityCapturePurposes.ReturnCitizenFront,
                    bookingId, booking.CustomerId, staffId, frontPath, now),
                BuildSession(IdentityCapturePurposes.ReturnCitizenBack,
                    bookingId, booking.CustomerId, staffId, backPath, now));
            await _dbContext.SaveChangesAsync(cancellationToken);
            persisted = true;

            foreach (var oldPath in oldPaths) _secureStorage.Delete(oldPath);

            await _auditService.WriteAsync(
                staffId,
                "CaptureReturnCitizenIdEvidence",
                nameof(Booking),
                bookingId.ToString(),
                "Nhân viên lưu hai mặt CCCD người trả để đối chiếu trực quan với hồ sơ KYC trước khi lập biên bản trả xe.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            return Json(new
            {
                saved = true,
                frontReady = true,
                backReady = true,
                frontUrl = Url.Action(nameof(CurrentReturnCitizen), new { bookingId, side = "front" }),
                backUrl = Url.Action(nameof(CurrentReturnCitizen), new { bookingId, side = "back" })
            });
        }
        catch
        {
            if (!persisted)
            {
                if (frontPath is not null) _secureStorage.Delete(frontPath);
                if (backPath is not null) _secureStorage.Delete(backPath);
            }
            throw;
        }
    }

    private IActionResult SecureImage(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) ||
            !_secureStorage.TryResolve(storedPath, out var fullPath, out var contentType))
        {
            return NotFound();
        }

        var stream = System.IO.File.Open(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        return File(stream, contentType);
    }

    private async Task<(IdentityCaptureSession? Front, IdentityCaptureSession? Back)> GetCurrentSessionsAsync(
        int bookingId,
        string customerId,
        string staffId,
        bool isReturn,
        CancellationToken cancellationToken)
    {
        var frontPurpose = isReturn
            ? IdentityCapturePurposes.ReturnCitizenFront
            : IdentityCapturePurposes.HandoverCitizenFront;
        var backPurpose = isReturn
            ? IdentityCapturePurposes.ReturnCitizenBack
            : IdentityCapturePurposes.HandoverCitizenBack;
        var now = DateTime.UtcNow;
        var sessions = await _dbContext.Set<IdentityCaptureSession>()
            .AsNoTracking()
            .Where(item =>
                item.BookingId == bookingId &&
                item.TargetCustomerId == customerId &&
                item.CreatedByUserId == staffId &&
                !item.ConsumedAt.HasValue &&
                item.CompletedAt.HasValue &&
                item.ExpiresAt > now &&
                item.ImagePath != null &&
                item.CaptureMethod == IdentityCaptureMethods.StaffCounterDocument &&
                (item.Purpose == frontPurpose || item.Purpose == backPurpose))
            .OrderByDescending(item => item.CompletedAt)
            .ToListAsync(cancellationToken);

        return (
            sessions.FirstOrDefault(item => item.Purpose == frontPurpose),
            sessions.FirstOrDefault(item => item.Purpose == backPurpose));
    }

    private static IdentityCaptureSession BuildSession(
        string purpose,
        int bookingId,
        string customerId,
        string staffId,
        string imagePath,
        DateTime now) =>
        new()
        {
            IdentityCaptureSessionId = Guid.NewGuid(),
            TokenHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Purpose = purpose,
            TargetCustomerId = customerId,
            BookingId = bookingId,
            CreatedByUserId = staffId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(IdentityCapturePolicy.CounterDocumentSessionLifetimeMinutes),
            CompletedAt = now,
            ImagePath = imagePath,
            CaptureMethod = IdentityCaptureMethods.StaffCounterDocument
        };
}
