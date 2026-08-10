using SmartCar.Application.Features.Audits;

namespace SmartCar.Web.ViewModels;

public sealed class AuditLogIndexViewModel
{
    public string? Search { get; init; }
    public string? UserId { get; init; }
    public string? Action { get; init; }
    public string? EntityName { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public AuditLogSearchResult Result { get; init; } = new(
        Array.Empty<AuditLogDto>(),
        0,
        1,
        50,
        Array.Empty<AuditUserFilterOption>(),
        Array.Empty<AuditValueFilterOption>(),
        Array.Empty<AuditValueFilterOption>());

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Search) ||
        !string.IsNullOrWhiteSpace(UserId) ||
        !string.IsNullOrWhiteSpace(Action) ||
        !string.IsNullOrWhiteSpace(EntityName) ||
        FromDate.HasValue ||
        ToDate.HasValue;
}
