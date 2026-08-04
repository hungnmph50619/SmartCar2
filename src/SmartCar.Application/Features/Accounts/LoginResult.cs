namespace SmartCar.Application.Features.Accounts;

public sealed class LoginResult
{
    private LoginResult(bool succeeded, bool isManager, string? errorMessage)
    {
        Succeeded = succeeded;
        IsManager = isManager;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public bool IsManager { get; }
    public string? ErrorMessage { get; }

    public static LoginResult Success(bool isManager) => new(true, isManager, null);
    public static LoginResult Failure(string errorMessage) => new(false, false, errorMessage);
}
