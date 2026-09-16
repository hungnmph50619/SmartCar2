namespace SmartCar.Web.ViewModels;

public sealed class IdentityCapturePageViewModel
{
    public Guid SessionId { get; init; }
    public string Token { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed class IdentityCaptureWidgetViewModel
{
    public string Purpose { get; init; } = string.Empty;
    public string HiddenFieldName { get; init; } = "FaceCaptureSessionId";
    public Guid? CompletedSessionId { get; init; }
    public int? BookingId { get; init; }
    public string? CustomerId { get; init; }
    public string Title { get; init; } = "Ảnh xác minh khuôn mặt";
    public string HelpText { get; init; } = "Chụp trực tiếp khuôn mặt người đang xác minh.";
    public bool AllowStaffFallback { get; init; }
}
