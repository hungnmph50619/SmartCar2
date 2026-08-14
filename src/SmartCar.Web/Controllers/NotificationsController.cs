using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Notifications;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class NotificationsController : Controller
{
    private static readonly HashSet<string> AdminWorkTitles =
        new(StringComparer.Ordinal)
        {
            "Hồ sơ KYC chờ duyệt",
            "CCCD chờ xác minh",
            "GPLX chờ xác minh",
            "CCCD cập nhật chờ duyệt",
            "GPLX cập nhật chờ duyệt",
            "Đơn thuê chờ xử lý",
            "Yêu cầu gia hạn chờ xử lý",
            "Thanh toán QR chờ xác nhận"
        };

    private readonly INotificationService _notificationService;

    public NotificationsController(
        INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? tab,
        CancellationToken cancellationToken)
    {
        var userId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        ViewBag.ActiveTab =
            tab is "work" or "unread" or "handled"
                ? tab
                : null;

        ViewBag.UnreadCount =
            await _notificationService
                .GetUnreadCountAsync(
                    userId,
                    cancellationToken);

        return View(
            await _notificationService.GetAsync(
                userId,
                cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(
        int id,
        CancellationToken cancellationToken)
    {
        var userId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        if (User.IsInRole(RoleNames.Admin))
        {
            var notification =
                (await _notificationService.GetAsync(
                    userId,
                    cancellationToken))
                .FirstOrDefault(item =>
                    item.NotificationId == id);

            if (notification is not null &&
                IsAdminWorkNotification(
                    notification.Title))
            {
                TempData["ErrorMessage"] =
                    "Đây là việc cần xử lý. Hãy hoàn tất nghiệp vụ thay vì chỉ đánh dấu đã đọc.";

                return RedirectToAction(
                    nameof(Index),
                    new { tab = "work" });
            }
        }

        await _notificationService.MarkReadAsync(
            id,
            userId,
            cancellationToken);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenProfileDocuments(
        int id,
        CancellationToken cancellationToken)
    {
        var userId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        await _notificationService.MarkReadAsync(
            id,
            userId,
            cancellationToken);

        return RedirectToAction(
            "Index",
            "Profile",
            new { tab = "documents" });
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

        // Work item: chỉ mở trang xử lý, không đánh dấu đã đọc.
        // Notification chỉ được đóng khi nghiệp vụ thực sự hoàn tất.
        return RedirectToAction(
            "Review",
            "AdminKyc",
            new { customerId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(
        CancellationToken cancellationToken)
    {
        var userId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        if (User.IsInRole(RoleNames.Admin))
        {
            var notifications =
                await _notificationService.GetAsync(
                    userId,
                    cancellationToken);

            foreach (var item in
                     notifications.Where(item =>
                         !item.IsRead &&
                         !IsAdminWorkNotification(
                             item.Title)))
            {
                await _notificationService
                    .MarkReadAsync(
                        item.NotificationId,
                        userId,
                        cancellationToken);
            }

            TempData["SuccessMessage"] =
                "Đã đánh dấu tất cả thông báo là đã đọc. " +
                "Các việc cần xử lý vẫn được giữ cho đến khi bạn hoàn tất nghiệp vụ.";
        }
        else
        {
            await _notificationService
                .MarkAllReadAsync(
                    userId,
                    cancellationToken);

            TempData["SuccessMessage"] =
                "Đã đánh dấu tất cả thông báo là đã đọc.";
        }

        return RedirectToAction(nameof(Index));
    }

    private static bool IsAdminWorkNotification(
        string title) =>
        AdminWorkTitles.Contains(
            GetRawTitle(title));

    private static string GetRawTitle(
        string title)
    {
        var separatorIndex = title.IndexOf('|');
        return separatorIndex > 0
            ? title[..separatorIndex]
            : title;
    }
}
