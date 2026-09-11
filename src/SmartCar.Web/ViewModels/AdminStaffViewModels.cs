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
    [Required(ErrorMessage = "Vui lòng nhập mã nhân viên.")]
    [StringLength(30, MinimumLength = 2, ErrorMessage = "Mã nhân viên phải từ 2 đến 30 ký tự.")]
    [Display(Name = "Mã nhân viên")]
    public string EmployeeCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập họ tên nhân viên.")]
    [StringLength(100, ErrorMessage = "Họ tên tối đa 100 ký tự.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại.")]
    [StringLength(30)]
    [Display(Name = "Số điện thoại")]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [StringLength(20)]
    [Display(Name = "Số CCCD")]
    public string CitizenIdNumber { get; set; } = string.Empty;

    [Display(Name = "Đã đối chiếu và xác nhận hồ sơ nhân viên")]
    public bool ConfirmInformation { get; set; }
}

public sealed class AdminStaffEditViewModel
{
    public string UserId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập mã nhân viên.")]
    [StringLength(30, MinimumLength = 2, ErrorMessage = "Mã nhân viên phải từ 2 đến 30 ký tự.")]
    [Display(Name = "Mã nhân viên")]
    public string EmployeeCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập họ tên nhân viên.")]
    [StringLength(100, ErrorMessage = "Họ tên tối đa 100 ký tự.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại.")]
    [StringLength(30)]
    [Display(Name = "Số điện thoại")]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [StringLength(20)]
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
