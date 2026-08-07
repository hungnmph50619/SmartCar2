namespace SmartCar.Domain.Constants;

public static class DocumentTypes
{
    // Giữ nguyên giá trị cũ để tương thích với dữ liệu hiện có.
    public const string CitizenId = "CCCD";
    public const string CitizenIdentityCard = CitizenId;
    public const string CitizenIdBack = "CCCD mặt sau";
    public const string DrivingLicense = "GPLX";
    public const string DrivingLicenseBack = "GPLX mặt sau";

    public static readonly IReadOnlyCollection<string> RequiredForRental =
        new[] { CitizenId, CitizenIdBack, DrivingLicense, DrivingLicenseBack };
}
