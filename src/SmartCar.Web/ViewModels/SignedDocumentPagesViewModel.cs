namespace SmartCar.Web.ViewModels;

public sealed class SignedDocumentPagesViewModel
{
    public int BookingId { get; init; }

    public string DocumentTitle { get; init; } = string.Empty;

    public bool IsHandover { get; init; }

    public bool IsVerified { get; init; }

    public DateTime? VerifiedAt { get; init; }

    public IReadOnlyList<string> Paths { get; init; }
        = Array.Empty<string>();
}