namespace SmartCar.Domain.Constants;

public static class IdentityCapturePurposes
{
    public const string Kyc = "Kyc";
    public const string Handover = "Handover";
    public const string Return = "Return";

    public static readonly string[] All = { Kyc, Handover, Return };
}

public static class IdentityCaptureMethods
{
    public const string Camera = "Camera";
    public const string MobileCamera = "MobileCamera";
    public const string StaffFallbackUpload = "StaffFallbackUpload";

    public static readonly string[] All = { Camera, MobileCamera, StaffFallbackUpload };
}

public static class IdentityCapturePolicy
{
    public const int SessionLifetimeMinutes = 10;
    public const long MaximumFaceImageBytes = 5 * 1024 * 1024;
}
