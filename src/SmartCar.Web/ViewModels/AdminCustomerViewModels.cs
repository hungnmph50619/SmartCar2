using System.ComponentModel.DataAnnotations;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class AdminCustomerIndexViewModel
{
    public string? Query { get; set; }
    public string? ProfileStatus { get; set; }
    public string? AccountStatus { get; set; }
    public int TotalCustomers { get; set; }
    public int PendingVerificationCount { get; set; }
    public int NeedResubmissionCount { get; set; }
    public int VerifiedCount { get; set; }
    public IReadOnlyList<AdminCustomerListItemViewModel> Customers { get; set; } =
        Array.Empty<AdminCustomerListItemViewModel>();
}

public sealed class AdminCustomerListItemViewModel
{
    public string CustomerId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ProfileStatusCode { get; set; } = "Missing";
    public string ProfileStatusText { get; set; } = "Chưa hoàn tất";
    public int BookingCount { get; set; }
    public bool HasActiveBooking { get; set; }
}

public sealed class AdminCustomerDetailsViewModel
{
    public string CustomerId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public string ActiveTab { get; set; } = "overview";
    public string ProfileStatusCode { get; set; } = "Missing";
    public string ProfileStatusText { get; set; } = "Chưa hoàn tất";
    public bool CanRent { get; set; }
    public int CompletedBookingCount { get; set; }
    public int ActiveBookingCount { get; set; }
    public int CancelledBookingCount { get; set; }
    public decimal TotalPaidAmount { get; set; }
    public decimal PendingPaymentAmount { get; set; }
    public IReadOnlyList<DocumentDto> Documents { get; set; } = Array.Empty<DocumentDto>();
    public IReadOnlyList<AdminCustomerBookingItemViewModel> Bookings { get; set; } =
        Array.Empty<AdminCustomerBookingItemViewModel>();
    public IReadOnlyList<AdminCustomerPaymentItemViewModel> Payments { get; set; } =
        Array.Empty<AdminCustomerPaymentItemViewModel>();
    public IReadOnlyList<AdminCustomerIncidentItemViewModel> Incidents { get; set; } =
        Array.Empty<AdminCustomerIncidentItemViewModel>();
}

public sealed class AdminCustomerBookingItemViewModel
{
    public int BookingId { get; set; }
    public string VehicleName { get; set; } = string.Empty;
    public string LicensePlate { get; set; } = string.Empty;
    public DateTime PickupDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public BookingStatus Status { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AdminCustomerPaymentItemViewModel
{
    public int PaymentId { get; set; }
    public int BookingId { get; set; }
    public PaymentType Type { get; set; }
    public PaymentStatus Status { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? TransactionCode { get; set; }
    public DateTime? PaidAt { get; set; }
}

public sealed class AdminCustomerIncidentItemViewModel
{
    public int IncidentId { get; set; }
    public int? BookingId { get; set; }
    public string VehicleName { get; set; } = string.Empty;
    public IncidentType Type { get; set; }
    public IncidentStatus Status { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal CustomerLiabilityAmount { get; set; }
}

public sealed class RequestDocumentResubmissionViewModel
{
    [Range(1, int.MaxValue)]
    public int DocumentId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn lý do yêu cầu gửi lại.")]
    [StringLength(300)]
    public string Reason { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Ghi chú không được vượt quá 500 ký tự.")]
    public string? AdditionalNote { get; set; }
}

public sealed class CustomerAccountStatusViewModel
{
    [Required]
    public string CustomerId { get; set; } = string.Empty;

    public bool Activate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập lý do thay đổi trạng thái tài khoản.")]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}
