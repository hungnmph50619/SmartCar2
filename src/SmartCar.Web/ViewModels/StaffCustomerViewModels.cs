using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SmartCar.Web.Validation;

namespace SmartCar.Web.ViewModels;

public sealed class StaffCreateCustomerViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên khách hàng.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Họ và tên phải có từ 2 đến 100 ký tự.")]
    [RegularExpression(
        @"^ *[A-Za-zÀ-ÖØ-öø-ÿĂăĐđĨĩŨũƠơƯưẠ-ỹ]+(?:(?: +|['’\-])[A-Za-zÀ-ÖØ-öø-ÿĂăĐđĨĩŨũƠơƯưẠ-ỹ]+)* *$",
        ErrorMessage = "Họ và tên chỉ được chứa chữ cái, khoảng trắng, dấu nháy hoặc dấu gạch nối.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại.")]
    [RegularExpression(
        @"^(?:0|\+84)(?:[ .-]?[0-9]){9}$",
        ErrorMessage = "Số điện thoại phải gồm 10 chữ số bắt đầu bằng 0 hoặc dùng mã quốc gia +84.")]
    [Display(Name = "Số điện thoại")]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
    [StringLength(150, ErrorMessage = "Email không được vượt quá 150 ký tự.")]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng đặt mật khẩu ban đầu.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Mật khẩu phải có ít nhất 8 ký tự.")]
    [RegularExpression(
        @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).{8,100}$",
        ErrorMessage = "Mật khẩu phải có chữ hoa, chữ thường, số và ký tự đặc biệt.")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu ban đầu")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Mật khẩu xác nhận không khớp.")]
    [Display(Name = "Xác nhận mật khẩu")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [RegularExpression(@"^\d{12}$", ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")]
    [Display(Name = "Số CCCD")]
    public string CitizenIdNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày sinh theo CCCD.")]
    [DataType(DataType.Date)]
    [Display(Name = "Ngày sinh")]
    public DateTime? CitizenIdDateOfBirth { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn giới tính theo CCCD.")]
    [StringLength(30)]
    [Display(Name = "Giới tính")]
    public string CitizenIdGender { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày cấp CCCD.")]
    [DataType(DataType.Date)]
    [Display(Name = "Ngày cấp CCCD")]
    public DateTime? CitizenIdIssuedDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn CCCD.")]
    [DataType(DataType.Date)]
    [Display(Name = "Ngày hết hạn CCCD")]
    public DateTime? CitizenIdExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập địa chỉ thường trú theo CCCD.")]
    [StringLength(300, ErrorMessage = "Địa chỉ thường trú tối đa 300 ký tự.")]
    [Display(Name = "Địa chỉ thường trú")]
    public string CitizenIdPermanentAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chụp/tải ảnh mặt trước CCCD.")]
    [Display(Name = "Mặt trước CCCD")]
    public IFormFile? CitizenIdFrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chụp/tải ảnh mặt sau CCCD.")]
    [Display(Name = "Mặt sau CCCD")]
    public IFormFile? CitizenIdBackImage { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập số GPLX.")]
    [StringLength(20, MinimumLength = 6, ErrorMessage = "Số GPLX phải có từ 6 đến 20 ký tự.")]
    [RegularExpression(@"^[A-Za-z0-9]+$", ErrorMessage = "Số GPLX chỉ được chứa chữ và số.")]
    [Display(Name = "Số GPLX")]
    public string DrivingLicenseNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập hạng GPLX.")]
    [StringLength(20, ErrorMessage = "Hạng GPLX tối đa 20 ký tự.")]
    [Display(Name = "Hạng GPLX")]
    public string DrivingLicenseClass { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày cấp GPLX.")]
    [DataType(DataType.Date)]
    [Display(Name = "Ngày cấp GPLX")]
    public DateTime? DrivingLicenseIssuedDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn GPLX.")]
    [DataType(DataType.Date)]
    [Display(Name = "Ngày hết hạn GPLX")]
    public DateTime? DrivingLicenseExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chụp/tải ảnh mặt trước GPLX.")]
    [Display(Name = "Mặt trước GPLX")]
    public IFormFile? DrivingLicenseFrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chụp/tải ảnh mặt sau GPLX.")]
    [Display(Name = "Mặt sau GPLX")]
    public IFormFile? DrivingLicenseBackImage { get; set; }

    [MustBeTrue(ErrorMessage = "Nhân viên phải trực tiếp đối chiếu khách với CCCD và GPLX bản gốc trước khi xác minh tại quầy.")]
    [Display(Name = "Đã trực tiếp đối chiếu khách với CCCD và GPLX bản gốc")]
    public bool OriginalDocumentsChecked { get; set; }

    [MustBeTrue(ErrorMessage = "Chỉ tạo tài khoản khi khách đã đọc và đồng ý điều khoản sử dụng/chính sách bảo mật.")]
    [Display(Name = "Khách đã đọc và đồng ý điều khoản")]
    public bool CustomerAcceptedTerms { get; set; }
}
