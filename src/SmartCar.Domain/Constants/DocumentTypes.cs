namespace SmartCar.Domain.Constants;

public static class DocumentTypes
{
    public const string CitizenId = "CCCD";
    public const string DrivingLicense = "GPLX";

    public static readonly IReadOnlyCollection<string> RequiredForRental =
        new[] { CitizenId, DrivingLicense };
}
