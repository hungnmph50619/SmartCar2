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
        "Đơn đã thanh toán - chuẩn bị xe|",
        "Đơn sẵn sàng bàn giao|",
        "Đơn chờ kiểm tra & quyết toán|",
        "Yêu cầu gia hạn chờ xử lý|",
        "Thanh toán QR chờ xác nhận|",
        "Khoản hoàn chờ xử lý|"
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

        await EnsureAdminWorkNotificationsAsync(
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

        await EnsureAdminWorkNotificationsAsync(
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
    /// Tạo/reopen các thông báo nghiệp vụ dựa trên trạng thái thật trong DB.
    /// Vì vậy Admin không bị bỏ sót việc chỉ vì một controller quên phát notification:
    /// đơn mới, thanh toán, chuẩn bị/bàn giao, kiểm tra sau trả và hoàn tiền đều
    /// tự xuất hiện ở chuông thông báo khi còn việc phải xử lý.
    /// </summary>
    private async Task EnsureAdminWorkNotificationsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (!await IsAdminAsync(userId, cancellationToken))
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
                CustomerName = _dbContext.Users
                    .Where(user => user.Id == booking.CustomerId)
                    .Select(user => user.FullName)
                    .FirstOrDefault() ?? "Khách hàng",
                VehicleName = booking.Vehicle.VehicleName,
                booking.PickupDate
            })
            .ToListAsync(cancellationToken);

        foreach (var booking in pendingBookings)
        {
            await EnsureWorkItemAsync(
                userId,
                $"Đơn thuê chờ xử lý|{booking.BookingId}",
                $"Đơn #{booking.BookingId} của {booking.CustomerName} - {booking.VehicleName} đang chờ xác nhận. Lịch nhận {booking.PickupDate:dd/MM/yyyy HH:mm}.",
                cancellationToken);
        }

        var preparedEntityIds = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == "VehiclePrepared" &&
                log.EntityName == nameof(Booking))
            .Select(log => log.EntityId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var preparedBookingIds = preparedEntityIds
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

        var paidBookings = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.Paid)
            .Select(booking => new
            {
                booking.BookingId,
                CustomerName = _dbContext.Users
                    .Where(user => user.Id == booking.CustomerId)
                    .Select(user => user.FullName)
                    .FirstOrDefault() ?? "Khách hàng",
                VehicleName = booking.Vehicle.VehicleName
            })
            .ToListAsync(cancellationToken);

        foreach (var booking in paidBookings.Where(item => !preparedBookingIds.Contains(item.BookingId)))
        {
            await EnsureWorkItemAsync(
                userId,
                $"Đơn đã thanh toán - chuẩn bị xe|{booking.BookingId}",
                $"Khách {booking.CustomerName} đã hoàn tất thanh toán đơn #{booking.BookingId} - {booking.VehicleName}. Admin cần kiểm tra và chuẩn bị xe.",
                cancellationToken);
        }

        var readyBookings = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.Status == BookingStatus.ReadyForPickup &&
                booking.Handover == null)
            .Select(booking => new
            {
                booking.BookingId,
                CustomerName = _dbContext.Users
                    .Where(user => user.Id == booking.CustomerId)
                    .Select(user => user.FullName)
                    .FirstOrDefault() ?? "Khách hàng",
                VehicleName = booking.Vehicle.VehicleName,
                booking.PickupDate
            })
            .ToListAsync(cancellationToken);

        foreach (var booking in readyBookings)
        {
            await EnsureWorkItemAsync(
                userId,
                $"Đơn sẵn sàng bàn giao|{booking.BookingId}",
                $"Đơn #{booking.BookingId} - {booking.VehicleName} của {booking.CustomerName} đang sẵn sàng bàn giao. Lịch nhận {booking.PickupDate:dd/MM/yyyy HH:mm}.",
                cancellationToken);
        }

        var inspectionBookings = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.PendingInspection)
            .Select(booking => new
            {
                booking.BookingId,
                VehicleName = booking.Vehicle.VehicleName,
                booking.AdditionalAmount
            })
            .ToListAsync(cancellationToken);

        foreach (var booking in inspectionBookings)
        {
            await EnsureWorkItemAsync(
                userId,
                $"Đơn chờ kiểm tra & quyết toán|{booking.BookingId}",
                booking.AdditionalAmount > 0
                    ? $"Đơn #{booking.BookingId} - {booking.VehicleName} đã trả xe, đang có {booking.AdditionalAmount:N0} đồng phụ phí. Kiểm tra căn cứ và quyết toán cọc."
                    : $"Đơn #{booking.BookingId} - {booking.VehicleName} đã trả xe. Nếu không phát sinh phụ phí, hoàn tất kiểm tra để tạo khoản hoàn cọc.",
                cancellationToken);
        }

        var pendingExtensions = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Where(extension => extension.Status == BookingExtensionStatus.Pending)
            .Select(extension => new
            {
                extension.BookingExtensionId,
                extension.BookingId,
                extension.RequestedReturnDate
            })
            .ToListAsync(cancellationToken);

        foreach (var extension in pendingExtensions)
        {
            await EnsureWorkItemAsync(
                userId,
                $"Yêu cầu gia hạn chờ xử lý|{extension.BookingExtensionId}",
                $"Đơn #{extension.BookingId} đang chờ duyệt gia hạn đến {extension.RequestedReturnDate:dd/MM/yyyy HH:mm}.",
                cancellationToken);
        }

        var pendingQrPayments = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Status == PaymentStatus.AwaitingConfirmation &&
                payment.Method == PaymentMethods.BankQr)
            .Select(payment => new
            {
                payment.PaymentId,
                payment.BookingId,
                payment.Type,
                payment.Amount
            })
            .ToListAsync(cancellationToken);

        var pendingInitialQrGroups = pendingQrPayments
            .Where(payment =>
                payment.Type == PaymentType.Rental ||
                payment.Type == PaymentType.Deposit)
            .GroupBy(payment => payment.BookingId)
            .ToList();

        foreach (var group in pendingInitialQrGroups)
        {
            var rentalAmount = group
                .Where(payment => payment.Type == PaymentType.Rental)
                .Sum(payment => payment.Amount);
            var depositAmount = group
                .Where(payment => payment.Type == PaymentType.Deposit)
                .Sum(payment => payment.Amount);
            var totalAmount = rentalAmount + depositAmount;

            await EnsureWorkItemAsync(
                userId,
                $"Thanh toán QR chờ xác nhận|B{group.Key}",
                $"Khách đã báo chuyển khoản cho đơn #{group.Key}: tổng {totalAmount:N0} đồng " +
                $"(tiền thuê/phí giao nhận {rentalAmount:N0} đồng + cọc bảo đảm {depositAmount:N0} đồng). " +
                "Cần đối soát giao dịch ngân hàng cho đơn.",
                cancellationToken);
        }

        foreach (var payment in pendingQrPayments.Where(payment =>
                     payment.Type != PaymentType.Rental &&
                     payment.Type != PaymentType.Deposit))
        {
            await EnsureWorkItemAsync(
                userId,
                $"Thanh toán QR chờ xác nhận|{payment.PaymentId}",
                $"Khách đã báo chuyển khoản cho đơn #{payment.BookingId}: {payment.Type} - {payment.Amount:N0} đồng. Cần đối soát giao dịch ngân hàng.",
                cancellationToken);
        }

        var pendingRefunds = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Status == PaymentStatus.AwaitingRefund &&
                (payment.Type == PaymentType.Refund || payment.Type == PaymentType.DepositRefund))
            .Select(payment => new
            {
                payment.PaymentId,
                payment.BookingId,
                payment.Type,
                payment.Amount,
                CustomerName = _dbContext.Users
                    .Where(user => user.Id == payment.Booking.CustomerId)
                    .Select(user => user.FullName)
                    .FirstOrDefault() ?? "Khách hàng"
            })
            .ToListAsync(cancellationToken);

        foreach (var payment in pendingRefunds)
        {
            var refundName = payment.Type == PaymentType.DepositRefund
                ? "hoàn cọc bảo đảm"
                : "hoàn tiền";

            await EnsureWorkItemAsync(
                userId,
                $"Khoản hoàn chờ xử lý|{payment.PaymentId}",
                $"Cần {refundName} {payment.Amount:N0} đồng cho {payment.CustomerName}, đơn #{payment.BookingId}. Chỉ xác nhận sau khi đã chuyển tiền thực tế.",
                cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureWorkItemAsync(
        string userId,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Notifications
            .FirstOrDefaultAsync(item =>
                item.UserId == userId &&
                item.Title == title,
                cancellationToken);

        if (existing is null)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = userId,
                Title = title,
                Message = message
            });
            return;
        }

        if (!string.Equals(existing.Message, message, StringComparison.Ordinal))
        {
            existing.Message = message;
        }

        if (existing.IsRead)
        {
            existing.IsRead = false;
            existing.ReadAt = null;
        }
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
        if (!await IsAdminAsync(userId, cancellationToken))
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
                return int.TryParse(key, out var pendingBookingId) &&
                       await _dbContext.Bookings
                           .AsNoTracking()
                           .AnyAsync(
                               booking =>
                                   booking.BookingId == pendingBookingId &&
                                   booking.Status == BookingStatus.PendingConfirmation,
                               cancellationToken);

            case "Đơn đã thanh toán - chuẩn bị xe":
                if (!int.TryParse(key, out var paidBookingId))
                {
                    return false;
                }

                var stillPaid = await _dbContext.Bookings
                    .AsNoTracking()
                    .AnyAsync(booking =>
                        booking.BookingId == paidBookingId &&
                        booking.Status == BookingStatus.Paid,
                        cancellationToken);

                if (!stillPaid)
                {
                    return false;
                }

                return !await _dbContext.AuditLogs
                    .AsNoTracking()
                    .AnyAsync(log =>
                        log.Action == "VehiclePrepared" &&
                        log.EntityName == nameof(Booking) &&
                        log.EntityId == key,
                        cancellationToken);

            case "Đơn sẵn sàng bàn giao":
                return int.TryParse(key, out var readyBookingId) &&
                       await _dbContext.Bookings
                           .AsNoTracking()
                           .AnyAsync(booking =>
                               booking.BookingId == readyBookingId &&
                               booking.Status == BookingStatus.ReadyForPickup &&
                               booking.Handover == null,
                               cancellationToken);

            case "Đơn chờ kiểm tra & quyết toán":
                return int.TryParse(key, out var inspectionBookingId) &&
                       await _dbContext.Bookings
                           .AsNoTracking()
                           .AnyAsync(booking =>
                               booking.BookingId == inspectionBookingId &&
                               booking.Status == BookingStatus.PendingInspection,
                               cancellationToken);

            case "Yêu cầu gia hạn chờ xử lý":
                return int.TryParse(key, out var extensionId) &&
                       await _dbContext.BookingExtensions
                           .AsNoTracking()
                           .AnyAsync(extension =>
                               extension.BookingExtensionId == extensionId &&
                               extension.Status == BookingExtensionStatus.Pending,
                               cancellationToken);

            case "Thanh toán QR chờ xác nhận":
                if (key.StartsWith("B", StringComparison.Ordinal) &&
                    int.TryParse(key[1..], out var paymentBookingId))
                {
                    return await _dbContext.Payments
                        .AsNoTracking()
                        .AnyAsync(payment =>
                            payment.BookingId == paymentBookingId &&
                            payment.Status == PaymentStatus.AwaitingConfirmation &&
                            payment.Method == PaymentMethods.BankQr &&
                            (payment.Type == PaymentType.Rental ||
                             payment.Type == PaymentType.Deposit),
                            cancellationToken);
                }

                if (!int.TryParse(key, out var paymentId))
                {
                    return false;
                }

                var legacyPayment = await _dbContext.Payments
                    .AsNoTracking()
                    .Where(payment => payment.PaymentId == paymentId)
                    .Select(payment => new
                    {
                        payment.Type,
                        payment.Status,
                        payment.Method
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (legacyPayment is null)
                {
                    return false;
                }

                // Rental + Deposit của một checkout ban đầu được gộp thành một
                // công việc theo BookingId. Notification cũ theo từng PaymentId
                // được đóng để Admin không phải xử lý hai thẻ cho cùng một đơn.
                if (legacyPayment.Type == PaymentType.Rental ||
                    legacyPayment.Type == PaymentType.Deposit)
                {
                    return false;
                }

                return legacyPayment.Status == PaymentStatus.AwaitingConfirmation &&
                       legacyPayment.Method == PaymentMethods.BankQr;

            case "Khoản hoàn chờ xử lý":
                return int.TryParse(key, out var refundPaymentId) &&
                       await _dbContext.Payments
                           .AsNoTracking()
                           .AnyAsync(payment =>
                               payment.PaymentId == refundPaymentId &&
                               payment.Status == PaymentStatus.AwaitingRefund &&
                               (payment.Type == PaymentType.Refund ||
                                payment.Type == PaymentType.DepositRefund),
                               cancellationToken);

            default:
                return false;
        }
    }

    private async Task<bool> IsAdminAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        var adminRoleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return false;
        }

        return await _dbContext.UserRoles
            .AnyAsync(item =>
                item.UserId == userId &&
                item.RoleId == adminRoleId,
                cancellationToken);
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
