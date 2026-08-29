namespace SmartCar.Application.Features.Accounts;

public sealed class LoginResult
{
    private LoginResult(
        bool succeeded,
        bool isAdmin,
        bool isStaff,
        string? errorMessage)
    {
        Succeeded = succeeded;
        IsAdmin = isAdmin;
        IsStaff = isStaff;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }
    public bool IsAdmin { get; }
    public bool IsStaff { get; }
    public string? ErrorMessage { get; }

    public static LoginResult Success(
        bool isAdmin,
        bool isStaff = false)
        => new(true, isAdmin, isStaff, null);

    public static LoginResult Failure(string errorMessage)
        => new(false, false, false, errorMessage);
}