namespace SmartCar.Application.Features.Accounts;

public sealed class LoginResult
{
    private LoginResult(bool succeeded, bool isAdmin, string? errorMessage)
    {
        Succeeded = succeeded;
        IsAdmin = isAdmin;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public bool IsAdmin { get; }
    public string? ErrorMessage { get; }

    public static LoginResult Success(bool isAdmin) => new(true, isAdmin, null);
    public static LoginResult Failure(string errorMessage) => new(false, false, errorMessage);
}
