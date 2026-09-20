using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class ReturnInspectionViewModel
{
    public int BookingId { get; init; }

    public string CustomerName { get; init; } = string.Empty;

    public string VehicleName { get; init; } = string.Empty;

    public string LicensePlate { get; init; } = string.Empty;

    public BookingStatus Status { get; init; }

    public decimal DepositAmount { get; init; }

    public decimal AdditionalAmount { get; init; }

    public bool AdditionalChargePaid { get; init; }

    public bool AdditionalChargeAwaitingConfirmation { get; init; }

    public bool AdditionalChargeFinalized { get; init; }

    public PaymentStatus? RefundStatus { get; init; }

    public decimal RefundAmount { get; init; }

    public IReadOnlyList<ChargeSummaryDto> AdditionalCharges { get; init; }
        = Array.Empty<ChargeSummaryDto>();

    public IReadOnlyList<OverdueImpactViewModel> OverdueImpacts { get; init; }
        = Array.Empty<OverdueImpactViewModel>();


    // ==============================
    // XÁC MINH NGHIỆP VỤ NHÂN VIÊN
    // ==============================

    public bool HandoverIdentityVerified { get; init; }

    public bool HandoverSignedDocumentVerified { get; init; }

    public bool ReturnIdentityVerified { get; init; }

    public bool ReturnSignedDocumentVerified { get; init; }


    public bool AllStaffChecksCompleted =>
        HandoverIdentityVerified &&
        HandoverSignedDocumentVerified &&
        ReturnIdentityVerified &&
        ReturnSignedDocumentVerified;


    public InspectionSnapshotViewModel Handover { get; init; } = new();

    public InspectionSnapshotViewModel Return { get; init; } = new();


    public int DrivenKilometers =>
        Math.Max(
            0,
            Return.Mileage - Handover.Mileage);


    public int? FuelDifferencePercent =>
        Handover.FuelPercent.HasValue &&
        Return.FuelPercent.HasValue

            ? Return.FuelPercent.Value -
              Handover.FuelPercent.Value

            : null;
}


public sealed class InspectionSnapshotViewModel
{
    public DateTime RecordedAt { get; init; }

    public int Mileage { get; init; }

    public string FuelLevel { get; init; } = string.Empty;

    public string? ExteriorCondition { get; init; }

    public string? InteriorCondition { get; init; }

    public string? Accessories { get; init; }

    public bool? HasDamage { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<string> ImagePaths { get; init; }
        = Array.Empty<string>();


    public int? FuelPercent =>
        ParseFuelPercent(FuelLevel);


    public string FuelDisplay =>
        FuelPercent.HasValue
            ? $"{FuelPercent.Value}%"
            : FuelLevel;


    private static int? ParseFuelPercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();

        if (normalized.EndsWith('%'))
        {
            normalized = normalized[..^1].Trim();
        }

        return int.TryParse(
                   normalized,
                   out var percent) &&
               percent >= 0 &&
               percent <= 100

            ? percent
            : null;
    }
}

public sealed record OverdueImpactViewModel(
    int BookingId,
    decimal CompensationAmount);
