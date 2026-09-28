using SmartCar.Application.Features.Bookings;

namespace SmartCar.Web.ViewModels;

public sealed class HandoverDocumentViewModel
{
    public BookingDetailsDto Booking { get; init; } = null!;
    public DateTime RecordedAt { get; init; }
    public int Mileage { get; init; }
    public string FuelLevel { get; init; } = string.Empty;
    public string? ExteriorCondition { get; init; }
    public string? InteriorCondition { get; init; }
    public string? Accessories { get; init; }
    public string? Notes { get; init; }
    public int IncludedKilometers { get; init; }
    public decimal ExcessKmFeePerKm { get; init; }
    public decimal LateReturnFeeMultiplier { get; init; }
    public string TrafficFineTerms { get; init; } = string.Empty;
    public string DamageCompensationTerms { get; init; } = string.Empty;
    public bool PenaltyPolicyAccepted { get; init; }
    public IReadOnlyList<string> ImagePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SignedDocumentPaths { get; init; } = Array.Empty<string>();
    public string? SignedDocumentPath { get; init; }
    public bool SignedDocumentVerified { get; init; }
    public DateTime? SignedDocumentVerifiedAt { get; init; }
}

public sealed class ReturnDocumentViewModel
{
    public BookingDetailsDto Booking { get; init; } = null!;
    public DateTime RecordedAt { get; init; }
    public int Mileage { get; init; }
    public string FuelLevel { get; init; } = string.Empty;
    public string? ExteriorCondition { get; init; }
    public string? InteriorCondition { get; init; }
    public string? Accessories { get; init; }
    public bool HasDamage { get; init; }
    public bool IsLateReturn { get; init; }
    public int LateMinutes { get; init; }
    public decimal LateFee { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> ImagePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SignedDocumentPaths { get; init; } = Array.Empty<string>();
    public string? SignedDocumentPath { get; init; }
    public bool SignedDocumentVerified { get; init; }
    public DateTime? SignedDocumentVerifiedAt { get; init; }
}

public sealed class TripRecordViewModel
{
    public BookingDetailsDto Booking { get; init; } = null!;
    public HandoverDocumentViewModel Handover { get; init; } = null!;
    public ReturnDocumentViewModel Return { get; init; } = null!;
    public IReadOnlyList<OverdueSettlementViewModel> OverdueSettlements { get; init; }
        = Array.Empty<OverdueSettlementViewModel>();

    public int DrivenKilometers => Math.Max(0, Return.Mileage - Handover.Mileage);

    public bool HasSignedHandover => Handover.SignedDocumentPaths.Count > 0 || !string.IsNullOrWhiteSpace(Handover.SignedDocumentPath);
    public bool HasSignedReturn => Return.SignedDocumentPaths.Count > 0 || !string.IsNullOrWhiteSpace(Return.SignedDocumentPath);
    public bool IsClosed => Booking.Status == SmartCar.Domain.Enums.BookingStatus.Completed;
}


public sealed record OverdueSettlementViewModel(
    int AffectedBookingId,
    decimal DepositDeductedAmount,
    decimal DebtAmount,
    decimal DebtPaidAmount,
    decimal DebtOpenAmount);

