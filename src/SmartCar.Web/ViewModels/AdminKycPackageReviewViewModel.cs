using SmartCar.Application.Features.Documents;

namespace SmartCar.Web.ViewModels;

public sealed class AdminKycPackageReviewViewModel
{
    public string CustomerId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public IReadOnlyList<DocumentDto> Documents { get; set; } = Array.Empty<DocumentDto>();
}
