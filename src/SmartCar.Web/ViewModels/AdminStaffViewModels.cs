using System.ComponentModel.DataAnnotations;

namespace SmartCar.Web.ViewModels;

public sealed class AdminStaffIndexViewModel
{
    public string? Query { get; set; }
    public string Status { get; set; } = "all";
    public int TotalStaffCount { get; set; }
    public int ActiveStaffCount { get; set; }
    public int InactiveStaffCount { get; set; }
    public int PendingFirstPasswordChangeCount { get; set; }
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
    public bool MustChangePassword { get; set; }
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

    [Display(Name = "Đã đối chiếu CCCD bản gốc")]
    public bool IdentityDocumentChecked { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mật khẩu cho nhân viên.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Mật khẩu phải có ít nhất 8 ký tự.")]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).{8,100}$", ErrorMessage = "Mật khẩu phải có chữ hoa, chữ thường, số và ký tự đặc biệt.")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu")]
    public string TemporaryPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập lại mật khẩu.")]
    [DataType(DataType.Password)]
    [Compare(nameof(TemporaryPassword), ErrorMessage = "Mật khẩu nhập lại không khớp.")]
    [Display(Name = "Nhập lại mật khẩu")]
    public string ConfirmTemporaryPassword { get; set; } = string.Empty;
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

    [Display(Name = "Đã đối chiếu lại CCCD bản gốc")]
    public bool IdentityDocumentChecked { get; set; }
}

public sealed class AdminStaffDetailsViewModel
{
    public string UserId { get; set; } = string.Empty;
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string MaskedCitizenIdNumber { get; set; } = "Chưa cập nhật";
    public bool HasCitizenIdNumber { get; set; }
    public bool IsActive { get; set; }
    public bool HasPassword { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedByName { get; set; }
}
