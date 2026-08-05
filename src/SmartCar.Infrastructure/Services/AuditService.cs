using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class AuditService : IAuditService
{
    private readonly ApplicationDbContext _dbContext;

    public AuditService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task WriteAsync(
        string? userId,
        string action,
        string entityName,
        string entityId,
        string description,
        string? oldValues = null,
        string? newValues = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
            Action = action.Trim(),
            EntityName = entityName.Trim(),
            EntityId = entityId.Trim(),
            Description = description.Trim(),
            OldValues = Normalize(oldValues),
            NewValues = Normalize(newValues),
            IpAddress = Normalize(ipAddress),
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogDto>> GetRecentAsync(
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 1000);

        return await _dbContext.AuditLogs
            .AsNoTracking()
            .OrderByDescending(log => log.CreatedAt)
            .Take(take)
            .Select(log => new AuditLogDto(
                log.AuditLogId,
                log.UserId,
                log.UserId == null
                    ? null
                    : _dbContext.Users
                        .Where(user => user.Id == log.UserId)
                        .Select(user => user.FullName)
                        .FirstOrDefault(),
                log.Action,
                log.EntityName,
                log.EntityId,
                log.Description,
                log.OldValues,
                log.NewValues,
                log.IpAddress,
                log.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
