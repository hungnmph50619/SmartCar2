using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Notifications;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class NotificationService : INotificationService
{
    private const int ExpiringSoonDays = 30;
    private const string AdminBookingWorkTitlePrefix = "Đơn #";
    private const string AdminBookingWorkTitleSuffix = " cần xác nhận";

    private readonly ApplicationDbContext _dbContext;

    public NotificationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDocumentExpiryNotificationsAsync(userId, cancellationToken);
        await EnsureAdminPendingBookingNotificationsAsync(userId, cancellationToken);

        return await _dbContext.Notifications
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAt)
            .Take(100)
            .Select(item => new NotificationDto(
                item.NotificationId,
                item.Title,
                item.Message,
                item.IsRead,
                item.CreatedAt,
                item.ReadAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDocumentExpiryNotificationsAsync(userId, cancellationToken);
        await EnsureAdminPendingBookingNotificationsAsync(userId, cancellationToken);

        return await _dbContext.Notifications.CountAsync(
            item => item.UserId == userId && !item.IsRead,
            cancellationToken);
    }

    public async Task<OperationResult> MarkReadAsync(
        int notificationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var notification = await _dbContext.Notifications
            .FirstOrDefaultAsync(item =>
                item.NotificationId == notificationId &&
                item.UserId == userId,
                cancellationToken);

        if (notification is null)
        {
            return OperationResult.Failure("Không tìm thấy thông báo.");
        }

        notification.IsRead = true;
        notification.ReadAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> MarkAllReadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var notifications = await _dbContext.Notifications
            .Where(item => item.UserId == userId && !item.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var notification in notifications)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    private async Task EnsureAdminPendingBookingNotificationsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var adminRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var isAdmin = await _dbContext.UserRoles
            .AsNoTracking()
            .AnyAsync(item =>
                item.UserId == userId &&
                item.RoleId == adminRoleId,
                cancellationToken);

        if (!isAdmin)
        {
            return;
        }

        var pendingBookings = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.PendingConfirmation)
            .OrderBy(booking => booking.CreatedAt)
            .Select(booking => new
            {
                booking.BookingId,
                booking.PickupDate,
                booking.PickupMethod,
                VehicleName = booking.Vehicle.VehicleName
            })
            .ToListAsync(cancellationToken);

        var workNotifications = await _dbContext.Notifications
            .Where(item =>
                item.UserId == userId &&
                item.Title.StartsWith(AdminBookingWorkTitlePrefix) &&
                item.Title.EndsWith(AdminBookingWorkTitleSuffix))
            .ToListAsync(cancellationToken);

        var pendingBookingIds = pendingBookings
            .Select(item => item.BookingId)
            .ToHashSet();

        var changed = false;

        foreach (var booking in pendingBookings)
        {
            var title = BuildAdminBookingWorkTitle(booking.BookingId);
            var existing = workNotifications.FirstOrDefault(item => item.Title == title);

            if (existing is null)
            {
                var pickupText = booking.PickupDate.ToString("dd/MM HH:mm");
                var pickupMethod = booking.PickupMethod == VehiclePickupMethod.Delivery
                    ? "Giao tận nơi"
                    : "Nhận tại cửa hàng";

                _dbContext.Notifications.Add(new Notification
                {
                    UserId = userId,
                    Title = title,
                    Message = $"{booking.VehicleName} · nhận {pickupText} · {pickupMethod}. Mở Quản lý đơn để xác nhận."
                });

                changed = true;
                continue;
            }

            // Đây là công việc cần xử lý, nên giữ trạng thái chưa đọc cho tới khi
            // đơn được xác nhận hoặc từ chối.
            if (existing.IsRead)
            {
                existing.IsRead = false;
                existing.ReadAt = null;
                changed = true;
            }
        }

        foreach (var notification in workNotifications.Where(item => !item.IsRead))
        {
            var bookingId = TryGetBookingIdFromAdminWorkTitle(notification.Title);
            if (bookingId.HasValue && !pendingBookingIds.Contains(bookingId.Value))
            {
                notification.IsRead = true;
                notification.ReadAt = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static string BuildAdminBookingWorkTitle(int bookingId) =>
        $"{AdminBookingWorkTitlePrefix}{bookingId}{AdminBookingWorkTitleSuffix}";

    private static int? TryGetBookingIdFromAdminWorkTitle(string title)
    {
        if (!title.StartsWith(AdminBookingWorkTitlePrefix, StringComparison.Ordinal) ||
            !title.EndsWith(AdminBookingWorkTitleSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var idText = title[
            AdminBookingWorkTitlePrefix.Length..
            ^AdminBookingWorkTitleSuffix.Length];

        return int.TryParse(idText, out var bookingId)
            ? bookingId
            : null;
    }

    private async Task EnsureDocumentExpiryNotificationsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == userId &&
                document.Status == DocumentStatus.Verified &&
                document.ExpiryDate.HasValue &&
                (document.DocumentType == DocumentTypes.CitizenId ||
                 document.DocumentType == DocumentTypes.DrivingLicense))
            .Select(document => new
            {
                document.DocumentType,
                document.ExpiryDate
            })
            .ToListAsync(cancellationToken);

        if (documents.Count == 0)
        {
            return;
        }

        var today = DateTime.Today;
        var pendingNotifications = new List<Notification>();

        foreach (var document in documents)
        {
            var expiryDate = document.ExpiryDate!.Value.Date;
            var remainingDays = (expiryDate - today).Days;
            if (remainingDays > ExpiringSoonDays)
            {
                continue;
            }

            var documentName = document.DocumentType == DocumentTypes.CitizenId
                ? "CCCD"
                : "GPLX";

            var isExpired = remainingDays < 0;
            var title = isExpired
                ? $"{documentName} đã hết hạn - cần cập nhật"
                : $"{documentName} sắp hết hạn - cần cập nhật";
            var message = isExpired
                ? $"{documentName} của bạn đã hết hạn ngày {expiryDate:dd/MM/yyyy}. Vui lòng cập nhật giấy tờ mới và chờ Quản trị viên xác minh lại trước khi thuê xe."
                : $"{documentName} của bạn sẽ hết hạn vào ngày {expiryDate:dd/MM/yyyy} (còn {remainingDays} ngày). Vui lòng chuẩn bị cập nhật giấy tờ mới để tránh gián đoạn việc thuê xe.";

            var exists = await _dbContext.Notifications
                .AsNoTracking()
                .AnyAsync(item =>
                    item.UserId == userId &&
                    item.Title == title &&
                    item.Message == message,
                    cancellationToken);

            if (!exists && !pendingNotifications.Any(item =>
                    item.Title == title && item.Message == message))
            {
                pendingNotifications.Add(new Notification
                {
                    UserId = userId,
                    Title = title,
                    Message = message
                });
            }
        }

        if (pendingNotifications.Count == 0)
        {
            return;
        }

        _dbContext.Notifications.AddRange(pendingNotifications);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
