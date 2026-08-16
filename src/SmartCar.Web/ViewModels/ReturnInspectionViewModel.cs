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
    public PaymentStatus? RefundStatus { get; init; }
    public decimal RefundAmount { get; init; }
    public InspectionSnapshotViewModel Handover { get; init; } = new();
    public InspectionSnapshotViewModel Return { get; init; } = new();
    public IReadOnlyList<ChargeSummaryDto> AdditionalCharges { get; init; }
        = Array.Empty<ChargeSummaryDto>();

    public int DrivenKilometers =>
        Math.Max(0, Return.Mileage - Handover.Mileage);
}

public sealed class InspectionSnapshotViewModel
{
    public DateTime RecordedAt { get; init; }
    public int Mileage { get; init; }
    public string FuelLevel { get; init; } = string.Empty;
    public bool? HasDamage { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> ImagePaths { get; init; }
        = Array.Empty<string>();
}
