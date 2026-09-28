namespace SmartCar.Domain.Constants;

public static class IdentityCapturePurposes
{
    public const string Kyc = "Kyc";
    public const string Handover = "Handover";
    public const string Return = "Return";
    public const string HandoverCitizenFront = "HandoverCitizenFront";
    public const string HandoverCitizenBack = "HandoverCitizenBack";
    public const string ReturnCitizenFront = "ReturnCitizenFront";
    public const string ReturnCitizenBack = "ReturnCitizenBack";

    public static readonly string[] All =
    {
        Kyc,
        Handover,
        Return,
        HandoverCitizenFront,
        HandoverCitizenBack,
        ReturnCitizenFront,
        ReturnCitizenBack
    };
}

public static class IdentityCaptureMethods
{
    public const string Camera = "Camera";
    public const string MobileCamera = "MobileCamera";
    public const string StaffFallbackUpload = "StaffFallbackUpload";
    public const string StaffCounterDocument = "StaffCounterDocument";

    // Face sessions only. Counter-document evidence is intentionally excluded so it
    // can never be mistaken for a face capture by the handover/return services.
    public static readonly string[] All = { Camera, MobileCamera, StaffFallbackUpload };
}

public static class IdentityCapturePolicy
{
    public const int SessionLifetimeMinutes = 10;
    public const int CounterDocumentSessionLifetimeMinutes = 120;
    public const long MaximumFaceImageBytes = 5 * 1024 * 1024;
    public const long MaximumCounterDocumentImageBytes = 5 * 1024 * 1024;
}
