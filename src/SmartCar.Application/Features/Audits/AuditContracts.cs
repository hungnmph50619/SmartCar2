namespace SmartCar.Application.Features.Audits;

public sealed record AuditLogDto(
    long AuditLogId,
    string? UserId,
    string? UserName,
    string Action,
    string EntityName,
    string EntityId,
    string Description,
    string? OldValues,
    string? NewValues,
    string? IpAddress,
    DateTime CreatedAt);

public interface IAuditService
{
    Task WriteAsync(
        string? userId,
        string action,
        string entityName,
        string entityId,
        string description,
        string? oldValues = null,
        string? newValues = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLogDto>> GetRecentAsync(
        int take = 200,
        CancellationToken cancellationToken = default);
}
