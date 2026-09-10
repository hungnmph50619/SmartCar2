using System.ComponentModel.DataAnnotations;
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

    [MustBeTrue(ErrorMessage = "Chỉ tạo tài khoản khi khách đã đọc và đồng ý điều khoản sử dụng/chính sách bảo mật.")]
    [Display(Name = "Khách đã đọc và đồng ý điều khoản")]
    public bool CustomerAcceptedTerms { get; set; }
}
