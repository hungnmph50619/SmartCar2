using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class IdentityCaptureController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ISecureDocumentStorage _storage;
    private readonly IAuditService _auditService;

    public IdentityCaptureController(
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        ISecureDocumentStorage storage,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _storage = storage;
        _auditService = auditService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSession(
        string purpose,
        int? bookingId,
        string? customerId,
        CancellationToken cancellationToken)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Unauthorized();
        }

        var target = await ResolveTargetAsync(
            purpose,
            bookingId,
            customerId,
            actorId,
            cancellationToken);
        if (!target.Succeeded)
        {
            return BadRequest(new { error = target.Error });
        }

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var session = new IdentityCaptureSession
        {
            TokenHash = HashToken(rawToken),
            Purpose = purpose,
            TargetCustomerId = target.CustomerId!,
            BookingId = bookingId,
            CreatedByUserId = actorId,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(IdentityCapturePolicy.SessionLifetimeMinutes)
        };

        _dbContext.Set<IdentityCaptureSession>().Add(session);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var captureUrl = Url.Action(
            nameof(Capture),
            "IdentityCapture",
            new { sessionId = session.IdentityCaptureSessionId, token = rawToken },
            Request.Scheme)!;
        var qrUrl = Url.Action(
            nameof(Qr),
            "IdentityCapture",
            new { sessionId = session.IdentityCaptureSessionId, token = rawToken },
            Request.Scheme)!;

        return Json(new
        {
            sessionId = session.IdentityCaptureSessionId,
            token = rawToken,
            captureUrl,
            qrUrl,
            expiresAtUtc = session.ExpiresAt
        });
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Capture(
        Guid sessionId,
        string token,
        CancellationToken cancellationToken)
    {
        var session = await FindValidTokenSessionAsync(sessionId, token, cancellationToken);
        if (session is null || !session.CanCapture(DateTime.UtcNow))
        {
            return View("CaptureExpired");
        }

        return View(new IdentityCapturePageViewModel
        {
            SessionId = session.IdentityCaptureSessionId,
            Token = token,
            Purpose = session.Purpose,
            ExpiresAtUtc = session.ExpiresAt
        });
    }

    [AllowAnonymous]
    [HttpPost]
    [RequestSizeLimit(IdentityCapturePolicy.MaximumFaceImageBytes + 512 * 1024)]
    public async Task<IActionResult> SubmitCamera(
        Guid sessionId,
        string token,
        IFormFile? image,
        bool mobileHandoff,
        CancellationToken cancellationToken)
    {
        var validationError = await ImageFileValidator.ValidateAsync(
            image,
            IdentityCapturePolicy.MaximumFaceImageBytes,
            cancellationToken);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var session = await FindValidTokenSessionAsync(sessionId, token, cancellationToken);
        if (session is null || !session.CanCapture(DateTime.UtcNow))
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { error = "Phiên chụp ảnh đã hết hạn hoặc đã được sử dụng." });
        }

        string? path = null;
        try
        {
            path = await _storage.SaveAsync(
                image!,
                $"identity-{session.TargetCustomerId}",
                cancellationToken);

            session.ImagePath = path;
            session.CaptureMethod = mobileHandoff
                ? IdentityCaptureMethods.MobileCamera
                : IdentityCaptureMethods.Camera;
            session.CompletedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _storage.Delete(path);
            }
            throw;
        }

        return Json(new
        {
            succeeded = true,
            sessionId = session.IdentityCaptureSessionId,
            imageUrl = Url.Action(nameof(Image), new { sessionId = session.IdentityCaptureSessionId })
        });
    }

    [HttpGet]
    public async Task<IActionResult> Status(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await _dbContext.Set<IdentityCaptureSession>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.IdentityCaptureSessionId == sessionId, cancellationToken);
        if (session is null)
        {
            return NotFound();
        }

        if (!CanCurrentUserRead(session))
        {
            return Forbid();
        }

        var expired = session.IsExpired(DateTime.UtcNow) && !session.CompletedAt.HasValue;
        return Json(new
        {
            completed = session.CompletedAt.HasValue && !string.IsNullOrWhiteSpace(session.ImagePath),
            expired,
            sessionId = session.IdentityCaptureSessionId,
            captureMethod = session.CaptureMethod,
            imageUrl = session.CompletedAt.HasValue
                ? Url.Action(nameof(Image), new { sessionId = session.IdentityCaptureSessionId })
                : null
        });
    }

    [HttpGet]
    public async Task<IActionResult> Image(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await _dbContext.Set<IdentityCaptureSession>()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.IdentityCaptureSessionId == sessionId, cancellationToken);
        if (session is null || !CanCurrentUserRead(session) || string.IsNullOrWhiteSpace(session.ImagePath))
        {
            return NotFound();
        }

        if (!_storage.TryResolve(session.ImagePath, out var fullPath, out var contentType))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(fullPath, contentType, enableRangeProcessing: false);
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Qr(
        Guid sessionId,
        string token,
        CancellationToken cancellationToken)
    {
        var session = await FindValidTokenSessionAsync(sessionId, token, cancellationToken);
        if (session is null || session.IsExpired(DateTime.UtcNow))
        {
            return NotFound();
        }

        var captureUrl = Url.Action(
            nameof(Capture),
            "IdentityCapture",
            new { sessionId, token },
            Request.Scheme)!;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(captureUrl, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return File(png.GetGraphic(8), "image/png");
    }

    [Authorize(Roles = RoleNames.Staff)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(IdentityCapturePolicy.MaximumFaceImageBytes + 512 * 1024)]
    public async Task<IActionResult> StaffFallback(
        string purpose,
        int? bookingId,
        string? customerId,
        string reason,
        IFormFile? image,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            return BadRequest(new { error = "Phải ghi rõ lý do dùng upload dự phòng (ít nhất 10 ký tự)." });
        }

        var validationError = await ImageFileValidator.ValidateAsync(
            image,
            IdentityCapturePolicy.MaximumFaceImageBytes,
            cancellationToken);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var target = await ResolveTargetAsync(
            purpose,
            bookingId,
            customerId,
            staffId,
            cancellationToken);
        if (!target.Succeeded)
        {
            return BadRequest(new { error = target.Error });
        }

        string? path = null;
        try
        {
            path = await _storage.SaveAsync(
                image!,
                $"identity-{target.CustomerId}",
                cancellationToken);
            var now = DateTime.UtcNow;
            var session = new IdentityCaptureSession
            {
                TokenHash = HashToken(WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))),
                Purpose = purpose,
                TargetCustomerId = target.CustomerId!,
                BookingId = bookingId,
                CreatedByUserId = staffId,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(IdentityCapturePolicy.SessionLifetimeMinutes),
                CompletedAt = now,
                ImagePath = path,
                CaptureMethod = IdentityCaptureMethods.StaffFallbackUpload,
                FallbackReason = reason.Trim()
            };

            _dbContext.Set<IdentityCaptureSession>().Add(session);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _auditService.WriteAsync(
                staffId,
                "StaffIdentityFaceFallbackUpload",
                nameof(IdentityCaptureSession),
                session.IdentityCaptureSessionId.ToString(),
                $"Staff dùng upload ảnh mặt dự phòng cho mục đích {purpose}" +
                (bookingId.HasValue ? $" của đơn #{bookingId.Value}" : string.Empty) +
                $". Lý do: {reason.Trim()}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            return Json(new
            {
                succeeded = true,
                sessionId = session.IdentityCaptureSessionId,
                imageUrl = Url.Action(nameof(Image), new { sessionId = session.IdentityCaptureSessionId })
            });
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _storage.Delete(path);
            }
            throw;
        }
    }

    private async Task<IdentityCaptureSession?> FindValidTokenSessionAsync(
        Guid sessionId,
        string? token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHash = HashToken(token);
        return await _dbContext.Set<IdentityCaptureSession>()
            .FirstOrDefaultAsync(item =>
                item.IdentityCaptureSessionId == sessionId &&
                item.TokenHash == tokenHash,
                cancellationToken);
    }

    private bool CanCurrentUserRead(IdentityCaptureSession session)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        return userId == session.TargetCustomerId ||
               userId == session.CreatedByUserId ||
               User.IsInRole(RoleNames.Staff) ||
               User.IsInRole(RoleNames.Admin);
    }

    private async Task<TargetResolution> ResolveTargetAsync(
        string purpose,
        int? bookingId,
        string? requestedCustomerId,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!IdentityCapturePurposes.All.Contains(purpose, StringComparer.Ordinal))
        {
            return TargetResolution.Failure("Mục đích chụp ảnh xác minh không hợp lệ.");
        }

        if (purpose == IdentityCapturePurposes.Kyc)
        {
            if (User.IsInRole(RoleNames.Customer))
            {
                return TargetResolution.Success(actorId);
            }

            if (!User.IsInRole(RoleNames.Staff) || string.IsNullOrWhiteSpace(requestedCustomerId))
            {
                return TargetResolution.Failure("Chỉ khách hàng tự chụp hoặc Staff hỗ trợ trực tiếp mới được tạo phiên KYC.");
            }

            var customer = await _userManager.FindByIdAsync(requestedCustomerId);
            if (customer is null || !customer.IsActive || !await _userManager.IsInRoleAsync(customer, RoleNames.Customer))
            {
                return TargetResolution.Failure("Khách hàng không hợp lệ hoặc đã bị khóa.");
            }

            return TargetResolution.Success(customer.Id);
        }

        if (!User.IsInRole(RoleNames.Staff) || !bookingId.HasValue)
        {
            return TargetResolution.Failure("Chỉ Staff đang xử lý đơn mới được tạo phiên chụp nhận/trả xe.");
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId.Value)
            .Select(item => new { item.CustomerId, item.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return TargetResolution.Failure("Không tìm thấy đơn thuê.");
        }

        if (purpose == IdentityCapturePurposes.Handover && booking.Status != BookingStatus.ReadyForPickup)
        {
            return TargetResolution.Failure("Chỉ đơn Sẵn sàng nhận xe mới được chụp người nhận.");
        }

        if (purpose == IdentityCapturePurposes.Return && booking.Status != BookingStatus.Rented)
        {
            return TargetResolution.Failure("Chỉ đơn Đang thuê mới được chụp người trả.");
        }

        return TargetResolution.Success(booking.CustomerId);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private sealed record TargetResolution(bool Succeeded, string? CustomerId, string? Error)
    {
        public static TargetResolution Success(string customerId) => new(true, customerId, null);
        public static TargetResolution Failure(string error) => new(false, null, error);
    }
}
