namespace SmartCar.Application.Common;

public sealed class OperationResult
{
    private OperationResult(bool succeeded, IReadOnlyCollection<string> errors)
    {
        Succeeded = succeeded;
        Errors = errors;
    }

    public bool Succeeded { get; }
    public IReadOnlyCollection<string> Errors { get; }

    public static OperationResult Success() => new(true, Array.Empty<string>());

    public static OperationResult Failure(params string[] errors) =>
        new(false, errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());

    public static OperationResult Failure(IEnumerable<string> errors) =>
        new(false, errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());
}
