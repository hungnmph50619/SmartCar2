using System.ComponentModel.DataAnnotations;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class StaffDashboardViewModel
{
    public int TodayPickups { get; init; }
    public int TodayReturns { get; init; }
    public int PendingInspections { get; init; }
    public int ApprovedRefundBookings { get; init; }
    public int PaidWaitingPreparation { get; init; }
    public IReadOnlyList<BookingListItemDto> WorkItems { get; init; } = Array.Empty<BookingListItemDto>();
}

public sealed class StaffBookingDetailsViewModel
{
    public BookingDetailsDto Booking { get; init; } = null!;
    public DateTime? StaffReviewedAt { get; init; }
    public bool HandoverIdentityVerified { get; init; }
    public string? HandoverIdentityVerifiedBy { get; init; }
    public DateTime? HandoverIdentityVerifiedAt { get; init; }
    public bool HandoverSignedDocumentVerified { get; init; }
    public string? HandoverSignedVerifiedBy { get; init; }
    public DateTime? HandoverSignedVerifiedAt { get; init; }
    public IReadOnlyList<string> HandoverSignedPaths { get; init; } = Array.Empty<string>();
    public bool ReturnIdentityVerified { get; init; }
    public string? ReturnIdentityVerifiedBy { get; init; }
    public DateTime? ReturnIdentityVerifiedAt { get; init; }
    public bool ReturnSignedDocumentVerified { get; init; }
    public string? ReturnSignedVerifiedBy { get; init; }
    public DateTime? ReturnSignedVerifiedAt { get; init; }
    public IReadOnlyList<string> ReturnSignedPaths { get; init; } = Array.Empty<string>();
    public bool HasApprovedRefund { get; init; }
    public bool HasUnapprovedRefund { get; init; }
    public decimal RefundAmount { get; init; }
}

public sealed class StaffCounterRentalViewModel
{
    [Required(ErrorMessage = "Vui lòng chọn khách hàng.")]
    public string CustomerId { get; set; } = string.Empty;

    [Range(1, int.MaxValue, ErrorMessage = "Vui lòng chọn xe.")]
    public int VehicleId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn thời gian nhận xe.")]
    public DateTime PickupDate { get; set; } = DateTime.Now.AddMinutes(10);

    [Required(ErrorMessage = "Vui lòng chọn thời gian trả xe.")]
    public DateTime ReturnDate { get; set; } = DateTime.Now.AddDays(1).AddMinutes(10);
}

public sealed class StaffRefundViewModel
{
    public int BookingId { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public decimal AwaitingApprovalAmount { get; init; }
    public bool HasAwaitingApproval => AwaitingApprovalAmount > 0m;
    public decimal OutstandingTrafficFineAmount { get; init; }
    public bool HasOutstandingTrafficFine => OutstandingTrafficFineAmount > 0m;
    public decimal UnfundedCompensationAmount { get; init; }
    public bool HasUnfundedCompensation => UnfundedCompensationAmount > 0m;
    public string? BankName { get; init; }
    public string? AccountNumber { get; init; }
    public string? AccountHolderName { get; init; }
    public PaymentStatus Status { get; init; }
    public IReadOnlyList<StaffRefundLineViewModel> Lines { get; init; } = Array.Empty<StaffRefundLineViewModel>();
}

public sealed record StaffRefundLineViewModel(
    int PaymentId,
    decimal Amount,
    string Method,
    PaymentStatus Status,
    string? TransactionCode,
    string? LedgerReference);
