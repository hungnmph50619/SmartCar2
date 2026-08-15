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

[Authorize(Roles = RoleNames.Customer)]
public sealed class CustomerReturnsController : Controller
{
    private readonly ApplicationDbContext _dbContext;

    public CustomerReturnsController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Review(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var bookingStatus = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId && item.CustomerId == customerId)
            .Select(item => (BookingStatus?)item.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (!bookingStatus.HasValue)
        {
            return NotFound();
        }

        if (bookingStatus.Value is not (BookingStatus.PendingInspection or BookingStatus.Completed))
        {
            TempData["ErrorMessage"] = "Biên bản đối chiếu trả xe chỉ khả dụng sau khi SmartCar thực tế tiếp nhận xe.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var model = await ReturnEvidenceHelper.BuildAsync(
            _dbContext,
            bookingId,
            customerId,
            cancellationToken);

        if (model is null)
        {
            return NotFound();
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở bước chờ kiểm tra sau trả xe.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var latestAction = await GetLatestReviewActionAsync(bookingId, cancellationToken);
        if (latestAction != ReturnEvidenceHelper.PendingAction)
        {
            TempData["ErrorMessage"] = latestAction switch
            {
                ReturnEvidenceHelper.AcceptedAction => "Bạn đã xác nhận hiện trạng trả xe trước đó.",
                ReturnEvidenceHelper.DisputedAction => "Bạn đã gửi yêu cầu xem xét. Hãy chờ SmartCar xử lý phản hồi.",
                ReturnEvidenceHelper.ResolvedAction => "Phản hồi về hiện trạng trả xe đã được SmartCar xử lý.",
                _ => "Biên bản này không thuộc luồng xác nhận hiện trạng mới."
            };
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var createdAt = DateTime.UtcNow;
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = customerId,
            Action = ReturnEvidenceHelper.AcceptedAction,
            EntityName = nameof(VehicleReturn),
            EntityId = bookingId.ToString(),
            Description =
                $"Khách đã xem ảnh bàn giao, ảnh bổ sung của khách (nếu có) và ảnh trả xe của đơn #{bookingId}; xác nhận hiện trạng trả xe để SmartCar tiếp tục kiểm tra, quyết toán.",
            NewValues = JsonSerializer.Serialize(new
            {
                bookingId,
                AcceptedAt = createdAt
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = createdAt
        });

        await AddAdminNotificationsAsync(
            $"Khách đã xác nhận hiện trạng trả xe|Booking:{bookingId}",
            $"Khách đã xem bộ ảnh giao ↔ trả của đơn #{bookingId} và chọn Đồng ý. Admin có thể tiếp tục xử lý phụ phí có căn cứ và hoàn tất kiểm tra.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã ghi nhận bạn đồng ý với hiện trạng xe được chụp khi SmartCar tiếp nhận lại.";
        return RedirectToAction(nameof(Review), new { bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dispute(
        int bookingId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length < 10 || normalizedReason.Length > 1000)
        {
            TempData["ErrorMessage"] = "Vui lòng mô tả điểm không đồng ý từ 10 đến 1000 ký tự.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở bước chờ kiểm tra sau trả xe.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var latestAction = await GetLatestReviewActionAsync(bookingId, cancellationToken);
        if (latestAction != ReturnEvidenceHelper.PendingAction)
        {
            TempData["ErrorMessage"] = "Biên bản này đã có phản hồi và không thể gửi phản hồi lần hai.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var createdAt = DateTime.UtcNow;
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = customerId,
            Action = ReturnEvidenceHelper.DisputedAction,
            EntityName = nameof(VehicleReturn),
            EntityId = bookingId.ToString(),
            Description =
                $"Khách không đồng ý với một phần hiện trạng/bằng chứng trả xe của đơn #{bookingId}. Lý do: {normalizedReason}",
            NewValues = JsonSerializer.Serialize(new
            {
                bookingId,
                Reason = normalizedReason,
                DisputedAt = createdAt
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = createdAt
        });

        await AddAdminNotificationsAsync(
            $"Khách yêu cầu xem xét hiện trạng trả xe|Booking:{bookingId}",
            $"Khách không đồng ý với một phần bộ ảnh/hiện trạng trả xe của đơn #{bookingId}. Lý do: {normalizedReason}. Không hoàn tất đơn hoặc quyết toán cọc cho đến khi phản hồi được xử lý.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã gửi yêu cầu xem xét. SmartCar sẽ đối chiếu lại ảnh trước–sau và căn cứ liên quan trước khi quyết toán.";
        return RedirectToAction(nameof(Review), new { bookingId });
    }

    private Task<string?> GetLatestReviewActionAsync(
        int bookingId,
        CancellationToken cancellationToken) =>
        _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(VehicleReturn) &&
                log.EntityId == bookingId.ToString() &&
                ReturnEvidenceHelper.ReviewActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => log.Action)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task AddAdminNotificationsAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var adminRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.RoleId == adminRoleId)
            .Select(userRole => userRole.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var adminId in adminIds)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            });
        }
    }
}
