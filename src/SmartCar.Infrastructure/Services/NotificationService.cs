using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Notifications;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class NotificationService : INotificationService
{
    private readonly ApplicationDbContext _dbContext;

    public NotificationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Notifications
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

    public Task<int> GetUnreadCountAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        _dbContext.Notifications.CountAsync(
            item => item.UserId == userId && !item.IsRead,
            cancellationToken);

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
}
