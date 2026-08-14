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
    public async Task<IActionResult> OpenAdminWork(
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

        var notification =
            (await _notificationService.GetAsync(
                userId,
                cancellationToken))
            .FirstOrDefault(item =>
                item.NotificationId == id);

        if (notification is null ||
            !IsAdminWorkNotification(
                notification.Title))
        {
            return RedirectToAction(nameof(Index));
        }

        // Chỉ ghi nhận Admin đã mở xem.
        // IsRead của work item vẫn giữ false cho tới khi nghiệp vụ hoàn tất.
        await _notificationService.MarkViewedAsync(
            id,
            userId,
            cancellationToken);

        var rawTitle = GetRawTitle(notification.Title);
        var workKey = GetWorkKey(notification.Title);

        switch (rawTitle)
        {
            case "Hồ sơ KYC chờ duyệt":
                return !string.IsNullOrWhiteSpace(workKey)
                    ? RedirectToAction(
                        "Review",
                        "AdminKyc",
                        new { customerId = workKey })
                    : RedirectToAction(
                        "Index",
                        "AdminKyc");

            case "CCCD chờ xác minh":
            case "GPLX chờ xác minh":
            case "CCCD cập nhật chờ duyệt":
            case "GPLX cập nhật chờ duyệt":
                return !string.IsNullOrWhiteSpace(workKey)
                    ? RedirectToAction(
                        "Details",
                        "AdminCustomers",
                        new
                        {
                            id = workKey,
                            tab = "documents"
                        })
                    : RedirectToAction(
                        "Index",
                        "AdminCustomers",
                        new { profileStatus = "Pending" });

            case "Đơn thuê chờ xử lý":
                return int.TryParse(
                        workKey,
                        out var bookingId)
                    ? RedirectToAction(
                        "Details",
                        "AdminBookings",
                        new { id = bookingId })
                    : RedirectToAction(
                        "Index",
                        "AdminBookings");

            case "Yêu cầu gia hạn chờ xử lý":
                return RedirectToAction(
                    "Index",
                    "AdminExtensions");

            case "Thanh toán QR chờ xác nhận":
                return RedirectToAction(
                    "Index",
                    "AdminPayments",
                    new { status = "AwaitingConfirmation" });

            default:
                return RedirectToAction(nameof(Index));
        }
    }

    [Authorize(Roles = RoleNames.Admin)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenKyc(
        int id,
        string customerId,
        CancellationToken cancellationToken)
    {
        var userId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return RedirectToAction(nameof(Index));
        }

        await _notificationService.MarkViewedAsync(
            id,
            userId,
            cancellationToken);

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
                         item.ReadAt is null &&
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

    private static string GetWorkKey(
        string title)
    {
        var separatorIndex = title.IndexOf('|');
        return separatorIndex > 0 &&
               separatorIndex < title.Length - 1
            ? title[(separatorIndex + 1)..]
            : string.Empty;
    }
}
