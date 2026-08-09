using SmartCar.Application.Features.Documents;

namespace SmartCar.Web.ViewModels;

public sealed class AdminKycPackageReviewViewModel
{
    public string CustomerId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public IReadOnlyList<DocumentDto> Documents { get; set; } = Array.Empty<DocumentDto>();
}

public sealed class AdminKycQueueViewModel
{
    public IReadOnlyList<AdminKycQueueItemViewModel> Items { get; set; } = Array.Empty<AdminKycQueueItemViewModel>();
}

public sealed class AdminKycQueueItemViewModel
{
    public string CustomerId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string? CustomerPhone { get; set; }
    public DateTime SubmittedAt { get; set; }
    public bool IsFullPackage { get; set; }
    public string PendingDocumentText { get; set; } = "Giấy tờ";
}
