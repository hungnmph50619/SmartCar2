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
                item.ReadAt == null,
            cancellationToken);
    }

    public async Task<OperationResult> MarkViewedAsync(
        int notificationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var notification =
            await _dbContext.Notifications
                .FirstOrDefaultAsync(
                    item =>
                        item.NotificationId == notificationId &&
                        item.UserId == userId,
                    cancellationToken);

        if (notification is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy thông báo.");
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }

        return OperationResult.Success();
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
                        item.NotificationId == notificationId &&
                        item.UserId == userId,
                    cancellationToken);

        if (notification is null)
        {
            return OperationResult.Failure(
                "Không tìm thấy thông báo.");
        }

        notification.IsRead = true;
        notification.ReadAt ??= DateTime.UtcNow;

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
            notification.ReadAt ??= DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return OperationResult.Success();
    }

    /// <summary>
    /// Work notification có hai trạng thái độc lập:
    /// ReadAt = Admin đã mở xem; IsRead = nghiệp vụ đã hoàn tất.
    /// Mỗi lần layout/trang notification hỏi dữ liệu, trạng thái thật trong DB
    /// được đối chiếu. Nếu nghiệp vụ vẫn Pending/AwaitingConfirmation thì một
    /// work item từng bị đóng nhầm sẽ được mở lại; khi nghiệp vụ hoàn tất thì
    /// work item mới chuyển sang đã xử lý.
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

        // Dashboard có thể phát hiện một khoản QR đang chờ trong khi dữ liệu
        // notification cũ bị thiếu. Tạo work item còn thiếu trước khi đối chiếu.
        await EnsurePendingQrWorkNotificationsAsync(
            userId,
            cancellationToken);

        var candidates =
            await _dbContext.Notifications
                .Where(item => item.UserId == userId)
                .ToListAsync(cancellationToken);

        var workItems = candidates
            .Where(item =>
                IsAdminWorkNotification(item.Title))
            .ToList();

        if (workItems.Count == 0)
        {
            return;
        }

        var changed = false;

        foreach (var item in workItems)
        {
            // Remove legacy Admin work items created before Staff review.
            if (GetRawTitle(item.Title) == "Đơn thuê chờ xử lý" &&
                int.TryParse(item.Title[(item.Title.IndexOf('|') + 1)..], out var bookingId) &&
                await _dbContext.Bookings.AsNoTracking().AnyAsync(
                    booking => booking.BookingId == bookingId &&
                               booking.Status == BookingStatus.PendingConfirmation &&
                               !booking.StaffReviewedAt.HasValue,
                    cancellationToken))
            {
                _dbContext.Notifications.Remove(item);
                changed = true;
                continue;
            }

            var isStillActive =
                await IsWorkItemStillActiveAsync(
                    item.Title,
                    cancellationToken);

            if (isStillActive)
            {
                // Work item có thể đã bị MarkRead/MarkAllRead trong dữ liệu cũ.
                // Nếu nghiệp vụ vẫn đang chờ thì phải đưa nó về Cần xử lý.
                if (item.IsRead)
                {
                    item.IsRead = false;
                    changed = true;
                }

                continue;
            }

            if (!item.IsRead)
            {
                item.IsRead = true;
                item.ReadAt ??= DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
    }

    private async Task EnsurePendingQrWorkNotificationsAsync(
        string adminId,
        CancellationToken cancellationToken)
    {
        var pendingQrPayments =
            await _dbContext.Payments
                .AsNoTracking()
                .Where(payment =>
                    payment.Status == PaymentStatus.AwaitingConfirmation &&
                    payment.Method == PaymentMethods.BankQr &&
                    payment.Type != PaymentType.Deposit)
                .Select(payment => new
                {
                    payment.PaymentId,
                    payment.BookingId,
                    payment.Type,
                    payment.Amount,
                    payment.Booking.CustomerId,
                    payment.Booking.Vehicle.VehicleName,
                    DepositAmount = payment.Booking.Payments
                        .Where(item =>
                            item.Type == PaymentType.Deposit &&
                            item.Status == PaymentStatus.AwaitingConfirmation &&
                            item.Method == PaymentMethods.BankQr)
                        .Sum(item => item.Amount)
                })
                .ToListAsync(cancellationToken);

        var existingNotifications =
            await _dbContext.Notifications
                .Where(item =>
                    item.UserId == adminId &&
                    item.Title.StartsWith(
                        "Thanh toán QR chờ xác nhận|"))
                .ToListAsync(cancellationToken);

        var existingByTitle = existingNotifications
            .GroupBy(item => item.Title, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        var customerIds = pendingQrPayments
            .Select(item => item.CustomerId)
            .Distinct()
            .ToList();

        var customerNames = await _dbContext.Users
            .AsNoTracking()
            .Where(user => customerIds.Contains(user.Id))
            .ToDictionaryAsync(
                user => user.Id,
                user => user.FullName,
                cancellationToken);

        var changed = false;

        foreach (var payment in pendingQrPayments)
        {
            var title =
                $"Thanh toán QR chờ xác nhận|{payment.PaymentId}";

            var totalSubmitted = payment.Type == PaymentType.Rental
                ? payment.Amount + payment.DepositAmount
                : payment.Amount;

            customerNames.TryGetValue(
                payment.CustomerId,
                out var customerName);

            customerName = string.IsNullOrWhiteSpace(customerName)
                ? "Khách hàng"
                : customerName;

            var message =
                $"{customerName} báo đã chuyển {totalSubmitted:N0} đồng cho đơn #{payment.BookingId} - {payment.VehicleName}. " +
                "Hãy kiểm tra và xác nhận thanh toán.";

            if (existingByTitle.TryGetValue(title, out var existing))
            {
                if (!string.Equals(
                        existing.Message,
                        message,
                        StringComparison.Ordinal))
                {
                    existing.Message = message;
                    changed = true;
                }

                if (existing.IsRead)
                {
                    existing.IsRead = false;
                    changed = true;
                }

                continue;
            }

            var notification = new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            };

            _dbContext.Notifications.Add(notification);
            existingByTitle[title] = notification;
            changed = true;
        }

        // Dữ liệu cũ có thể đã tạo một work item riêng cho tiền cọc.
        // Cọc được chuyển cùng khoản Rental nên không phải một việc độc lập.
        foreach (var notification in existingNotifications)
        {
            var separatorIndex = notification.Title.IndexOf('|');
            if (separatorIndex <= 0 ||
                !int.TryParse(
                    notification.Title[(separatorIndex + 1)..],
                    out var paymentId))
            {
                continue;
            }

            var isDepositPayment = await _dbContext.Payments
                .AsNoTracking()
                .AnyAsync(
                    payment =>
                        payment.PaymentId == paymentId &&
                        payment.Type == PaymentType.Deposit,
                    cancellationToken);

            if (isDepositPayment && !notification.IsRead)
            {
                notification.IsRead = true;
                notification.ReadAt ??= DateTime.UtcNow;
                changed = true;
            }
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
        var prefix = GetRawTitle(title);

        if (separatorIndex <= 0 ||
            separatorIndex >= title.Length - 1)
        {
            // Một số dữ liệu test/legacy cũ chưa có business key.
            // Giữ chúng ở trạng thái cần xử lý thay vì tự đóng sai.
            return prefix is
                "CCCD cập nhật chờ duyệt" or
                "GPLX cập nhật chờ duyệt";
        }

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
            case "CCCD cập nhật chờ duyệt":
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
            case "GPLX cập nhật chờ duyệt":
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
                                           .PendingConfirmation &&
                                   booking.StaffReviewedAt.HasValue,
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
                                   payment.Type !=
                                       PaymentType.Deposit &&
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
