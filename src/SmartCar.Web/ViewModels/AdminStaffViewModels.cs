using System.ComponentModel.DataAnnotations;

namespace SmartCar.Web.ViewModels;

public sealed class AdminStaffIndexViewModel
{
    public string? Query { get; set; }
    public string Status { get; set; } = "all";
    public IReadOnlyList<AdminStaffListItemViewModel> Staff { get; set; } = Array.Empty<AdminStaffListItemViewModel>();
}

public sealed class AdminStaffListItemViewModel
{
    public string UserId { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? CitizenIdNumber { get; set; }
    public bool IsActive { get; set; }
    public bool HasPassword { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AdminStaffCreateViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên nhân viên.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Họ và tên phải từ 2 đến 100 ký tự.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [RegularExpression(@"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$", ErrorMessage = "Email chỉ được dùng chữ, số và các dấu . _ - hợp lệ; không được chứa ký tự đặc biệt khác.")]
    [StringLength(256, ErrorMessage = "Email tối đa 256 ký tự.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại.")]
    [RegularExpression(@"^0\d{9}$", ErrorMessage = "Số điện thoại phải gồm đúng 10 chữ số, bắt đầu bằng số 0 và không được chứa chữ hoặc ký tự đặc biệt.")]
    [Display(Name = "Số điện thoại")]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [RegularExpression(@"^\d{12}$", ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số và không được chứa chữ hoặc ký tự đặc biệt.")]
    [Display(Name = "Số CCCD")]
    public string CitizenIdNumber { get; set; } = string.Empty;
}

public sealed class AdminStaffEditViewModel
{
    public string UserId { get; set; } = string.Empty;

    [Display(Name = "Mã nhân viên")]
    public string EmployeeCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập họ và tên nhân viên.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Họ và tên phải từ 2 đến 100 ký tự.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [RegularExpression(@"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$", ErrorMessage = "Email chỉ được dùng chữ, số và các dấu . _ - hợp lệ; không được chứa ký tự đặc biệt khác.")]
    [StringLength(256, ErrorMessage = "Email tối đa 256 ký tự.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại.")]
    [RegularExpression(@"^0\d{9}$", ErrorMessage = "Số điện thoại phải gồm đúng 10 chữ số, bắt đầu bằng số 0 và không được chứa chữ hoặc ký tự đặc biệt.")]
    [Display(Name = "Số điện thoại")]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [RegularExpression(@"^\d{12}$", ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số và không được chứa chữ hoặc ký tự đặc biệt.")]
    [Display(Name = "Số CCCD")]
    public string CitizenIdNumber { get; set; } = string.Empty;
}

public sealed class AdminStaffDetailsViewModel
{
    public string UserId { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? CitizenIdNumber { get; set; }
    public bool IsActive { get; set; }
    public bool HasPassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedByName { get; set; }
}
