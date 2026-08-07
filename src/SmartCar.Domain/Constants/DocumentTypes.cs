namespace SmartCar.Domain.Constants;

public static class DocumentTypes
{
    // Giữ nguyên giá trị "CCCD" để tương thích với dữ liệu đã có.
    // Trong giao diện, loại này được hiểu là CCCD mặt trước.
    public const string CitizenId = "CCCD";
    public const string CitizenIdentityCard = CitizenId;
    public const string CitizenIdBack = "CCCD mặt sau";
    public const string DrivingLicense = "GPLX";

    public static readonly IReadOnlyCollection<string> RequiredForRental =
        new[] { CitizenId, CitizenIdBack, DrivingLicense };
}
