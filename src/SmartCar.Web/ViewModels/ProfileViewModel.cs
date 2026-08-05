namespace SmartCar.Web.ViewModels;

public sealed class ProfileViewModel
{
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string PhoneNumber { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
