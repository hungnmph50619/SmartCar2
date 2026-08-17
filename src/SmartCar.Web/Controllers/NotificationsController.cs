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
    private const string BookingWorkPrefix = "Đơn #";
    private const string BookingWorkSuffix = " cần xác nhận";

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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenProfileDocuments(
        int id,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        await _notificationService.MarkReadAsync(id, userId, cancellationToken);
        return RedirectToAction("Index", "Profile", new { tab = "documents" });
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

        // KYC là công việc quản trị: chỉ được đánh dấu xử lý sau khi Admin
        // duyệt hoặc yêu cầu khách cập nhật hồ sơ.
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
                         !item.Title.StartsWith(KycWorkPrefix, StringComparison.Ordinal) &&
                         !IsBookingWorkTitle(item.Title)))
            {
                await _notificationService.MarkReadAsync(item.NotificationId, userId, cancellationToken);
            }

            TempData["SuccessMessage"] =
                "Đã đánh dấu các thông báo thường là đã đọc. Công việc đang chờ xử lý vẫn được giữ lại.";
        }
        else
        {
            await _notificationService.MarkAllReadAsync(userId, cancellationToken);
            TempData["SuccessMessage"] = "Đã đánh dấu tất cả thông báo là đã đọc.";
        }

        return RedirectToAction(nameof(Index));
    }

    private static bool IsBookingWorkTitle(string title) =>
        title.StartsWith(BookingWorkPrefix, StringComparison.Ordinal) &&
        title.EndsWith(BookingWorkSuffix, StringComparison.Ordinal);
}
