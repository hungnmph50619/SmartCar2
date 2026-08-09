namespace SmartCar.Web.ViewModels;

public sealed class KycPackageSubmitViewModel
{
    public CitizenIdVerificationViewModel CitizenIdVerification { get; set; } = new();

    public DrivingLicenseVerificationViewModel DrivingLicenseVerification { get; set; } = new();
}
