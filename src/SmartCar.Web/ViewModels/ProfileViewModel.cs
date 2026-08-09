using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SmartCar.Application.Features.Documents;
using SmartCar.Web.Services;

namespace SmartCar.Web.ViewModels;

public sealed class ProfileViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên.")]
    [StringLength(150, ErrorMessage = "Họ và tên không được vượt quá 150 ký tự.")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại liên hệ.")]
    [RegularExpression(@"^(0|\+84)[0-9]{9}$", ErrorMessage = "Số điện thoại phải gồm 10 chữ số và bắt đầu bằng 0, hoặc dùng mã quốc gia +84.")]
    [Display(Name = "Số điện thoại liên hệ")]
    public string PhoneNumber { get; set; } = string.Empty;

    [StringLength(300, ErrorMessage = "Địa chỉ không được vượt quá 300 ký tự.")]
    [Display(Name = "Địa chỉ")]
    public string Address { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string ActiveTab { get; set; } = "profile";

    [ValidateNever]
    public DocumentUploadViewModel DocumentUpload { get; set; } = new();

    [ValidateNever]
    public CitizenIdVerificationViewModel CitizenIdVerification { get; set; } = new();

    [ValidateNever]
    public DrivingLicenseVerificationViewModel DrivingLicenseVerification { get; set; } = new();

    [ValidateNever]
    public IReadOnlyList<DocumentDto> Documents { get; set; } = Array.Empty<DocumentDto>();

    [ValidateNever]
    public BankAccountViewModel BankAccount { get; set; } = new();

    [ValidateNever]
    public IReadOnlyList<BankOption> AvailableBanks { get; set; } = Array.Empty<BankOption>();

    public bool? BankHolderMatchesKyc { get; set; }
}

public sealed class BankAccountViewModel
{
    [Required(ErrorMessage = "Vui lòng chọn ngân hàng.")]
    [StringLength(20)]
    [Display(Name = "Ngân hàng")]
    public string BankCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số tài khoản.")]
    [RegularExpression(@"^[0-9]{6,20}$", ErrorMessage = "Số tài khoản phải gồm từ 6 đến 20 chữ số.")]
    [Display(Name = "Số tài khoản")]
    public string AccountNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên chủ tài khoản.")]
    [StringLength(150, ErrorMessage = "Tên chủ tài khoản không được vượt quá 150 ký tự.")]
    [Display(Name = "Chủ tài khoản")]
    public string AccountHolderName { get; set; } = string.Empty;

    public string BankName { get; set; } = string.Empty;
    public string MaskedAccountNumber { get; set; } = string.Empty;
    public bool HasSavedAccount { get; set; }
}
