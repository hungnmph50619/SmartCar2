using System.ComponentModel.DataAnnotations;

namespace SmartCar.Web.ViewModels;

public class CustomerAccountDashboardViewModel
{
    public string ActiveSection { get; set; } = "profile";
    public CustomerAccountProfileViewModel Profile { get; set; } = new();
    public IReadOnlyList<CustomerDocumentItemViewModel> Documents { get; set; } = Array.Empty<CustomerDocumentItemViewModel>();
    public IReadOnlyList<CustomerBookingItemViewModel> Bookings { get; set; } = Array.Empty<CustomerBookingItemViewModel>();
    public IReadOnlyList<CustomerPaymentItemViewModel> Payments { get; set; } = Array.Empty<CustomerPaymentItemViewModel>();
    public IReadOnlyList<CustomerNotificationItemViewModel> Notifications { get; set; } = Array.Empty<CustomerNotificationItemViewModel>();
    public int CompletedBookingCount { get; set; }
    public int UnreadNotificationCount { get; set; }
    public int VerifiedDocumentCount { get; set; }
}

public class CustomerAccountProfileViewModel
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public string? AvatarPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Initial => string.IsNullOrWhiteSpace(FullName)
        ? "K"
        : FullName.Trim()[0].ToString().ToUpperInvariant();
}

public class UpdateCustomerProfileViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên.")]
    [StringLength(100, ErrorMessage = "Họ và tên không được vượt quá 100 ký tự.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Phone(ErrorMessage = "Số điện thoại không hợp lệ.")]
    [StringLength(30)]
    [Display(Name = "Số điện thoại")]
    public string? PhoneNumber { get; set; }

    [StringLength(250, ErrorMessage = "Địa chỉ không được vượt quá 250 ký tự.")]
    [Display(Name = "Địa chỉ giao nhận mặc định")]
    public string? Address { get; set; }

    public string ReturnSection { get; set; } = "profile";
}

public class ChangeCustomerPasswordViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu hiện tại.")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu hiện tại")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới.")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu mới phải có ít nhất 6 ký tự.")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu mới")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu mới.")]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "Mật khẩu xác nhận không khớp.")]
    [Display(Name = "Xác nhận mật khẩu mới")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class CustomerDocumentItemViewModel
{
    public string DocumentType { get; set; } = string.Empty;
    public string TypeLabel { get; set; } = string.Empty;
    public string? DocumentNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? ImagePath { get; set; }
    public string StatusText { get; set; } = "Chưa cập nhật";
    public string StatusClass { get; set; } = "neutral";
    public string? RejectionReason { get; set; }
    public bool IsUploaded => !string.IsNullOrWhiteSpace(ImagePath);
}

public class CustomerBookingItemViewModel
{
    public int BookingId { get; set; }
    public string BookingCode => $"SC{BookingId:000000}";
    public string VehicleName { get; set; } = string.Empty;
    public string LicensePlate { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public DateTime PickupDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public decimal TotalAmount { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string StatusClass { get; set; } = "neutral";
    public string NextActionText { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public string? CancelReason { get; set; }
    public bool HasPayment { get; set; }
    public string PaymentStatusText { get; set; } = "Chưa thanh toán";
}

public class CustomerPaymentItemViewModel
{
    public int PaymentId { get; set; }
    public int BookingId { get; set; }
    public string BookingCode => $"SC{BookingId:000000}";
    public string VehicleName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string StatusClass { get; set; } = "neutral";
    public DateTime? PaidAt { get; set; }
    public string? TransactionCode { get; set; }
}

public class CustomerNotificationItemViewModel
{
    public int NotificationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}
