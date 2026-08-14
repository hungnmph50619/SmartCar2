using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Notifications;

public sealed record NotificationDto(
    int NotificationId,
    string Title,
    string Message,
    bool IsRead,
    DateTime CreatedAt,
    DateTime? ReadAt);

public interface INotificationService
{
    Task<IReadOnlyList<NotificationDto>> GetAsync(
        string userId,
        CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(
        string userId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> MarkViewedAsync(
        int notificationId,
        string userId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> MarkReadAsync(
        int notificationId,
        string userId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> MarkAllReadAsync(
        string userId,
        CancellationToken cancellationToken = default);
}
