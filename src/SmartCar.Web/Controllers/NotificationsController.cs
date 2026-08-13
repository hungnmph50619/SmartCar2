using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Notifications;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Controllers;

[Authorize]
public sealed class NotificationsController : Controller
{
    private static readonly string[] AdminWorkPrefixes =
    {
        "Hồ sơ KYC chờ duyệt|",
        "CCCD chờ xác minh|",
        "GPLX chờ xác minh|",
        "Đơn thuê chờ xử lý|",
        "Yêu cầu gia hạn chờ xử lý|",
        "Thanh toán QR chờ xác nhận|"
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
        // NotificationService sẽ tự đóng khi trạng thái nghiệp vụ
        // thực sự không còn Pending.
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
                "Đã đánh dấu các thông báo thường là đã đọc. " +
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
        AdminWorkPrefixes.Any(prefix =>
            title.StartsWith(
                prefix,
                StringComparison.Ordinal));
}
