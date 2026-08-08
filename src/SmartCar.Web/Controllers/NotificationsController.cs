using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Notifications;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class NotificationsController : Controller
{
    private const string KycWorkPrefix = "Hồ sơ KYC chờ duyệt|";
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        ViewBag.UnreadCount = await _notificationService.GetUnreadCountAsync(
            userId,
            cancellationToken);
        return View(await _notificationService.GetAsync(userId, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        await _notificationService.MarkReadAsync(id, userId, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult OpenKyc(
        int id,
        string customerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return RedirectToAction(nameof(Index));
        }

        // KYC is a work item, not just an informational notification. Opening the
        // review page must not clear the alert. It is marked handled only after
        // the admin approves the package or requests resubmission.
        return RedirectToAction("Review", "AdminKyc", new { customerId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        if (User.IsInRole(RoleNames.Admin))
        {
            var notifications = await _notificationService.GetAsync(userId, cancellationToken);
            foreach (var item in notifications.Where(item =>
                         !item.IsRead &&
                         !item.Title.StartsWith(KycWorkPrefix, StringComparison.Ordinal)))
            {
                await _notificationService.MarkReadAsync(item.NotificationId, userId, cancellationToken);
            }

            TempData["SuccessMessage"] = "Đã đánh dấu các thông báo thông thường là đã đọc. Hồ sơ KYC chờ duyệt vẫn được giữ cho đến khi xử lý.";
        }
        else
        {
            await _notificationService.MarkAllReadAsync(userId, cancellationToken);
            TempData["SuccessMessage"] = "Đã đánh dấu tất cả thông báo là đã đọc.";
        }

        return RedirectToAction(nameof(Index));
    }
}
