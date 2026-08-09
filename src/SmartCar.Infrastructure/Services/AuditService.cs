using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class AuditService : IAuditService
{
    private const string SystemUserFilter = "__system__";
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

        return await Project(_dbContext.AuditLogs.AsNoTracking())
            .OrderByDescending(log => log.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<AuditLogSearchResult> SearchAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 10, 200);
        var logs = _dbContext.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var keyword = query.Search.Trim();
            logs = logs.Where(log =>
                log.Description.Contains(keyword) ||
                log.Action.Contains(keyword) ||
                log.EntityName.Contains(keyword) ||
                log.EntityId.Contains(keyword) ||
                (log.IpAddress != null && log.IpAddress.Contains(keyword)) ||
                (log.UserId != null && _dbContext.Users.Any(user =>
                    user.Id == log.UserId &&
                    (user.FullName.Contains(keyword) ||
                     (user.Email != null && user.Email.Contains(keyword))))));
        }

        if (!string.IsNullOrWhiteSpace(query.UserId))
        {
            logs = query.UserId == SystemUserFilter
                ? logs.Where(log => log.UserId == null)
                : logs.Where(log => log.UserId == query.UserId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            logs = logs.Where(log => log.Action == query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityName))
        {
            logs = logs.Where(log => log.EntityName == query.EntityName);
        }

        if (query.FromDate.HasValue)
        {
            logs = logs.Where(log => log.CreatedAt >= query.FromDate.Value);
        }

        if (query.ToDate.HasValue)
        {
            logs = logs.Where(log => log.CreatedAt < query.ToDate.Value);
        }

        var totalCount = await logs.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        var items = await Project(logs)
            .OrderByDescending(log => log.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var userCounts = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => log.UserId != null)
            .GroupBy(log => log.UserId!)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ToListAsync(cancellationToken);

        var userIds = userCounts.Select(item => item.UserId).ToList();
        var userNames = await _dbContext.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.FullName, user.Email })
            .ToDictionaryAsync(
                user => user.Id,
                user => string.IsNullOrWhiteSpace(user.FullName)
                    ? user.Email ?? user.Id
                    : user.FullName,
                cancellationToken);

        var users = userCounts
            .Select(item => new AuditUserFilterOption(
                item.UserId,
                userNames.TryGetValue(item.UserId, out var displayName) ? displayName : item.UserId,
                item.Count))
            .ToList();

        var systemCount = await _dbContext.AuditLogs
            .AsNoTracking()
            .CountAsync(log => log.UserId == null, cancellationToken);
        if (systemCount > 0)
        {
            users.Insert(0, new AuditUserFilterOption(SystemUserFilter, "Hệ thống", systemCount));
        }

        var actions = await _dbContext.AuditLogs
            .AsNoTracking()
            .GroupBy(log => log.Action)
            .Select(group => new AuditValueFilterOption(group.Key, group.Count()))
            .OrderBy(option => option.Value)
            .ToListAsync(cancellationToken);

        var entities = await _dbContext.AuditLogs
            .AsNoTracking()
            .GroupBy(log => log.EntityName)
            .Select(group => new AuditValueFilterOption(group.Key, group.Count()))
            .OrderBy(option => option.Value)
            .ToListAsync(cancellationToken);

        return new AuditLogSearchResult(
            items,
            totalCount,
            page,
            pageSize,
            users,
            actions,
            entities);
    }

    public Task<AuditLogDto?> GetByIdAsync(
        long auditLogId,
        CancellationToken cancellationToken = default) =>
        Project(_dbContext.AuditLogs.AsNoTracking().Where(log => log.AuditLogId == auditLogId))
            .FirstOrDefaultAsync(cancellationToken);

    private IQueryable<AuditLogDto> Project(IQueryable<AuditLog> logs) =>
        logs.Select(log => new AuditLogDto(
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
            log.CreatedAt));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
