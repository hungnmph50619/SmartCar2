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

    private static readonly string[] AdminWorkPrefixes =
    {
        "Hồ sơ KYC chờ duyệt|",
        "CCCD chờ xác minh|",
        "GPLX chờ xác minh|",
        "Đơn thuê chờ xử lý|",
        "Yêu cầu gia hạn chờ xử lý|",
        "Thanh toán QR chờ xác nhận|"
    };

    private readonly ApplicationDbContext _dbContext;

    public NotificationService(
        ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDocumentExpiryNotificationsAsync(
            userId,
            cancellationToken);

        await ReconcileAdminWorkNotificationsAsync(
            userId,
            cancellationToken);

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
        await EnsureDocumentExpiryNotificationsAsync(
            userId,
            cancellationToken);

        await ReconcileAdminWorkNotificationsAsync(
            userId,
            cancellationToken);

        return await _dbContext.Notifications.CountAsync(
            item =>
                item.UserId == userId &&
                !item.IsRead,
            cancellationToken);
    }

    public async Task<OperationResult> MarkReadAsync(
        int notificationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var notification =
            await _dbContext.Notifications
                .FirstOrDefaultAsync(
                    item =>
                        item.NotificationId ==
                        notificationId &&
                        item.UserId == userId,
                    cancellationToken);

        if (notification is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy thông báo.");
        }

        notification.IsRead = true;
        notification.ReadAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return OperationResult.Success();
    }

    public async Task<OperationResult> MarkAllReadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var notifications =
            await _dbContext.Notifications
                .Where(item =>
                    item.UserId == userId &&
                    !item.IsRead)
                .ToListAsync(cancellationToken);

        foreach (var notification in notifications)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return OperationResult.Success();
    }

    /// <summary>
    /// Work notification không được đóng chỉ vì Admin đã mở xem.
    /// Mỗi lần layout/trang notification hỏi số badge, trạng thái thật trong DB
    /// được đối chiếu. Chỉ khi nghiệp vụ không còn Pending/AwaitingConfirmation
    /// thì notification mới tự chuyển sang đã xử lý.
    /// </summary>
    private async Task ReconcileAdminWorkNotificationsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var adminRoleId =
            await _dbContext.Roles
                .Where(role => role.Name == RoleNames.Admin)
                .Select(role => role.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var isAdmin =
            await _dbContext.UserRoles
                .AnyAsync(
                    item =>
                        item.UserId == userId &&
                        item.RoleId == adminRoleId,
                    cancellationToken);

        if (!isAdmin)
        {
            return;
        }

        var unread =
            await _dbContext.Notifications
                .Where(item =>
                    item.UserId == userId &&
                    !item.IsRead)
                .ToListAsync(cancellationToken);

        var workItems = unread
            .Where(item =>
                AdminWorkPrefixes.Any(prefix =>
                    item.Title.StartsWith(
                        prefix,
                        StringComparison.Ordinal)))
            .ToList();

        if (workItems.Count == 0)
        {
            return;
        }

        var changed = false;

        foreach (var item in workItems)
        {
            if (await IsWorkItemStillActiveAsync(
                    item.Title,
                    cancellationToken))
            {
                continue;
            }

            item.IsRead = true;
            item.ReadAt = DateTime.UtcNow;
            changed = true;
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
    }

    private async Task<bool> IsWorkItemStillActiveAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var separatorIndex = title.IndexOf('|');
        if (separatorIndex <= 0 ||
            separatorIndex >= title.Length - 1)
        {
            return false;
        }

        var prefix = title[..separatorIndex];
        var key = title[(separatorIndex + 1)..];

        switch (prefix)
        {
            case "Hồ sơ KYC chờ duyệt":
            {
                var requiredTypes = new[]
                {
                    DocumentTypes.CitizenId,
                    DocumentTypes.CitizenIdBack,
                    DocumentTypes.DrivingLicense,
                    DocumentTypes.DrivingLicenseBack
                };

                var pendingTypeCount =
                    await _dbContext.CustomerDocuments
                        .AsNoTracking()
                        .Where(document =>
                            document.CustomerId == key &&
                            document.Status ==
                                DocumentStatus.Pending &&
                            requiredTypes.Contains(
                                document.DocumentType))
                        .Select(document =>
                            document.DocumentType)
                        .Distinct()
                        .CountAsync(cancellationToken);

                return pendingTypeCount ==
                       requiredTypes.Length;
            }

            case "CCCD chờ xác minh":
                return await _dbContext.CustomerDocuments
                    .AsNoTracking()
                    .AnyAsync(
                        document =>
                            document.CustomerId == key &&
                            document.Status ==
                                DocumentStatus.Pending &&
                            (document.DocumentType ==
                                 DocumentTypes.CitizenId ||
                             document.DocumentType ==
                                 DocumentTypes.CitizenIdBack),
                        cancellationToken);

            case "GPLX chờ xác minh":
                return await _dbContext.CustomerDocuments
                    .AsNoTracking()
                    .AnyAsync(
                        document =>
                            document.CustomerId == key &&
                            document.Status ==
                                DocumentStatus.Pending &&
                            (document.DocumentType ==
                                 DocumentTypes.DrivingLicense ||
                             document.DocumentType ==
                                 DocumentTypes.DrivingLicenseBack),
                        cancellationToken);

            case "Đơn thuê chờ xử lý":
                return int.TryParse(key, out var bookingId) &&
                       await _dbContext.Bookings
                           .AsNoTracking()
                           .AnyAsync(
                               booking =>
                                   booking.BookingId ==
                                       bookingId &&
                                   booking.Status ==
                                       BookingStatus
                                           .PendingConfirmation,
                               cancellationToken);

            case "Yêu cầu gia hạn chờ xử lý":
                return int.TryParse(
                           key,
                           out var extensionId) &&
                       await _dbContext.BookingExtensions
                           .AsNoTracking()
                           .AnyAsync(
                               extension =>
                                   extension
                                       .BookingExtensionId ==
                                   extensionId &&
                                   extension.Status ==
                                       BookingExtensionStatus
                                           .Pending,
                               cancellationToken);

            case "Thanh toán QR chờ xác nhận":
                return int.TryParse(
                           key,
                           out var paymentId) &&
                       await _dbContext.Payments
                           .AsNoTracking()
                           .AnyAsync(
                               payment =>
                                   payment.PaymentId ==
                                       paymentId &&
                                   payment.Status ==
                                       PaymentStatus
                                           .AwaitingConfirmation &&
                                   payment.Method ==
                                       PaymentMethods.BankQr,
                               cancellationToken);

            default:
                return false;
        }
    }

    private async Task EnsureDocumentExpiryNotificationsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var documents =
            await _dbContext.CustomerDocuments
                .AsNoTracking()
                .Where(document =>
                    document.CustomerId == userId &&
                    document.Status ==
                        DocumentStatus.Verified &&
                    document.ExpiryDate.HasValue &&
                    (document.DocumentType ==
                         DocumentTypes.CitizenId ||
                     document.DocumentType ==
                         DocumentTypes.DrivingLicense))
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
        var pendingNotifications =
            new List<Notification>();

        foreach (var document in documents)
        {
            var expiryDate =
                document.ExpiryDate!.Value.Date;

            var remainingDays =
                (expiryDate - today).Days;

            if (remainingDays > ExpiringSoonDays)
            {
                continue;
            }

            var documentName =
                document.DocumentType ==
                DocumentTypes.CitizenId
                    ? "CCCD"
                    : "GPLX";

            var isExpired = remainingDays < 0;

            var title = isExpired
                ? $"{documentName} đã hết hạn - cần cập nhật"
                : $"{documentName} sắp hết hạn - cần cập nhật";

            var message = isExpired
                ? $"{documentName} của bạn đã hết hạn ngày {expiryDate:dd/MM/yyyy}. " +
                  "Vui lòng cập nhật giấy tờ mới và chờ Quản trị viên xác minh lại trước khi thuê xe."
                : $"{documentName} của bạn sẽ hết hạn vào ngày {expiryDate:dd/MM/yyyy} " +
                  $"(còn {remainingDays} ngày). Vui lòng chuẩn bị cập nhật giấy tờ mới " +
                  "để tránh gián đoạn việc thuê xe.";

            var exists =
                await _dbContext.Notifications
                    .AsNoTracking()
                    .AnyAsync(
                        item =>
                            item.UserId == userId &&
                            item.Title == title &&
                            item.Message == message,
                        cancellationToken);

            if (!exists &&
                !pendingNotifications.Any(item =>
                    item.Title == title &&
                    item.Message == message))
            {
                pendingNotifications.Add(
                    new Notification
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

        _dbContext.Notifications.AddRange(
            pendingNotifications);

        await _dbContext.SaveChangesAsync(
            cancellationToken);
    }
}
