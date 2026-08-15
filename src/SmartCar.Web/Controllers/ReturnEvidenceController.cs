using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReturnEvidenceController : Controller
{
    private readonly ApplicationDbContext _dbContext;

    public ReturnEvidenceController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Review(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var model = await ReturnEvidenceHelper.BuildAsync(
            _dbContext,
            bookingId,
            customerId: null,
            cancellationToken);

        if (model is null)
        {
            return NotFound();
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(
        int bookingId,
        string? resolutionNote,
        CancellationToken cancellationToken)
    {
        var normalizedNote = resolutionNote?.Trim() ?? string.Empty;
        if (normalizedNote.Length < 10 || normalizedNote.Length > 1000)
        {
            TempData["ErrorMessage"] = "Vui lòng ghi rõ kết quả đối chiếu/xử lý tranh chấp từ 10 đến 1000 ký tự.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            TempData["ErrorMessage"] = "Chỉ đơn đang chờ kiểm tra mới được ghi nhận kết quả xử lý tranh chấp hiện trạng.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var latestReview = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(VehicleReturn) &&
                log.EntityId == bookingId.ToString() &&
                ReturnEvidenceHelper.ReviewActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => log.Action)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestReview != ReturnEvidenceHelper.DisputedAction)
        {
            TempData["ErrorMessage"] = "Hiện không có phản hồi tranh chấp của khách cần xử lý.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var createdAt = DateTime.UtcNow;
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = adminId,
            Action = ReturnEvidenceHelper.ResolvedAction,
            EntityName = nameof(VehicleReturn),
            EntityId = bookingId.ToString(),
            Description =
                $"Admin đã đối chiếu và ghi nhận kết quả xử lý phản hồi hiện trạng trả xe của đơn #{bookingId}. Kết quả/căn cứ: {normalizedNote}",
            NewValues = JsonSerializer.Serialize(new
            {
                bookingId,
                ResolutionNote = normalizedNote,
                ResolvedAt = createdAt
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = createdAt
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "SmartCar đã xử lý phản hồi hiện trạng trả xe",
            Message =
                $"Phản hồi của bạn về đơn #{bookingId} đã được SmartCar đối chiếu. Kết quả ghi nhận: {normalizedNote}. " +
                "Hồ sơ ảnh trước–sau và lịch sử phản hồi vẫn được lưu để truy vết."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã ghi nhận kết quả xử lý phản hồi. Đơn có thể tiếp tục kiểm tra/quyết toán theo các căn cứ đã lưu.";
        return RedirectToAction(nameof(Review), new { bookingId });
    }
}
