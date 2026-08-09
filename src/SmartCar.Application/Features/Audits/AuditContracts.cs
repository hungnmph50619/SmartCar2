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

public sealed record AuditLogQuery(
    string? Search = null,
    string? UserId = null,
    string? Action = null,
    string? EntityName = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int Page = 1,
    int PageSize = 50);

public sealed record AuditUserFilterOption(
    string UserId,
    string DisplayName,
    int Count);

public sealed record AuditValueFilterOption(
    string Value,
    int Count);

public sealed record AuditLogSearchResult(
    IReadOnlyList<AuditLogDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<AuditUserFilterOption> Users,
    IReadOnlyList<AuditValueFilterOption> Actions,
    IReadOnlyList<AuditValueFilterOption> Entities)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

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

    Task<AuditLogSearchResult> SearchAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default);

    Task<AuditLogDto?> GetByIdAsync(
        long auditLogId,
        CancellationToken cancellationToken = default);
}
