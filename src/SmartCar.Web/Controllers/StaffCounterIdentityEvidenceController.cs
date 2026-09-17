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
            cancellationToken);

        return Json(new
        {
            bookingId,
            allowed = booking.Status == BookingStatus.ReadyForPickup,
            frontReady = sessions.Front is not null,
            backReady = sessions.Back is not null,
            ready = sessions.Front is not null && sessions.Back is not null
        });
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

            foreach (var old in previous)
            {
                _secureStorage.Delete(old.ImagePath);
            }
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

            await _auditService.WriteAsync(
                staffId,
                "CaptureHandoverCitizenIdEvidence",
                nameof(Booking),
                booking.BookingId.ToString(),
                "Nhân viên chụp/lưu CCCD mặt trước và mặt sau của khách đang có mặt tại quầy để đối chiếu trước khi bàn giao xe.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            return Json(new { saved = true, frontReady = true, backReady = true });
        }
        catch
        {
            if (frontPath is not null) _secureStorage.Delete(frontPath);
            if (backPath is not null) _secureStorage.Delete(backPath);
            throw;
        }
    }

    private async Task<(IdentityCaptureSession? Front, IdentityCaptureSession? Back)> GetCurrentSessionsAsync(
        int bookingId,
        string customerId,
        string staffId,
        CancellationToken cancellationToken)
    {
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
                (item.Purpose == IdentityCapturePurposes.HandoverCitizenFront ||
                 item.Purpose == IdentityCapturePurposes.HandoverCitizenBack))
            .OrderByDescending(item => item.CompletedAt)
            .ToListAsync(cancellationToken);

        return (
            sessions.FirstOrDefault(item => item.Purpose == IdentityCapturePurposes.HandoverCitizenFront),
            sessions.FirstOrDefault(item => item.Purpose == IdentityCapturePurposes.HandoverCitizenBack));
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
