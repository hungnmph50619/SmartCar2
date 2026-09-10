using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class TrafficFinePaymentViewModel
{
    public int PaymentId { get; init; }
    public int BookingId { get; init; }
    public int? VehicleIncidentId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public DateTime? OccurredAt { get; init; }
    public string? Description { get; init; }
    public string? EvidencePaths { get; init; }
    public decimal OfficialFineAmount { get; init; }
    public decimal Amount { get; init; }
    public PaymentStatus Status { get; init; }
    public DateTime? PaidAt { get; init; }
    public string? TransactionCode { get; init; }
}

public sealed class TrafficFineIndexViewModel
{
    public IReadOnlyList<TrafficFinePaymentViewModel> Items { get; init; }
        = Array.Empty<TrafficFinePaymentViewModel>();

    public string BankName { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string AccountHolder { get; init; } = string.Empty;
    public string QrImagePath { get; init; } = string.Empty;

    public decimal OutstandingAmount => Items
        .Where(item => item.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation or PaymentStatus.Failed)
        .Sum(item => item.Amount);
}
