using SmartCar.Domain.Constants;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SmartCar.Application.Features.Documents;
using SmartCar.Web.Services;

namespace SmartCar.Web.ViewModels;

public sealed class ProfileViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên.")]
    [StringLength(ProfileInputRules.FullNameMaxLength, MinimumLength = 2, ErrorMessage = "Họ và tên phải từ 2 đến 100 ký tự.")]
    [ValidFullName]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại liên hệ.")]
    [RegularExpression(ProfileInputRules.PhonePattern, ErrorMessage = ProfileInputRules.PhoneError)]
    [Display(Name = "Số điện thoại liên hệ")]
    public string PhoneNumber { get; set; } = string.Empty;

    [StringLength(ProfileInputRules.AddressMaxLength, ErrorMessage = "Địa chỉ không được vượt quá 250 ký tự.")]
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
    [RegularExpression(@"^[\p{L}]+(?:[ ]+[\p{L}]+)*$", ErrorMessage = "Tên chủ tài khoản chỉ được chứa chữ cái và khoảng trắng.")]
    [Display(Name = "Chủ tài khoản")]
    public string AccountHolderName { get; set; } = string.Empty;

    public string BankName { get; set; } = string.Empty;
    public string MaskedAccountNumber { get; set; } = string.Empty;
    public bool HasSavedAccount { get; set; }
}

