using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace SmartCar.Web.ViewModels;

public sealed class KycPackageSubmitViewModel
{
    public KycCitizenIdInputViewModel CitizenIdVerification { get; set; } = new();

    public KycDrivingLicenseInputViewModel DrivingLicenseVerification { get; set; } = new();

    public bool ConfirmSamePerson { get; set; }
}

public sealed class KycCitizenIdInputViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên trên CCCD.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Họ và tên phải có từ 2 đến 100 ký tự.")]
    [RegularExpression(
        @"^ *[A-Za-zÀ-ÖØ-öø-ÿĂăĐđĨĩŨũƠơƯưẠ-ỹ]+(?:(?: +|['’\-])[A-Za-zÀ-ÖØ-öø-ÿĂăĐđĨĩŨũƠơƯưẠ-ỹ]+)* *$",
        ErrorMessage = "Họ và tên chỉ được chứa chữ cái, khoảng trắng, dấu nháy hoặc dấu gạch nối.")]
    [Display(Name = "Họ và tên trên CCCD")]
    public string FullNameOnDocument { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [RegularExpression(@"^[0-9]{12}$", ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")]
    [Display(Name = "Số CCCD")]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày sinh.")]
    [Display(Name = "Ngày sinh")]
    public DateTime? DateOfBirth { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn giới tính.")]
    [RegularExpression(@"^(Nam|Nữ|Khác)$", ErrorMessage = "Giới tính không hợp lệ.")]
    [StringLength(20)]
    [Display(Name = "Giới tính")]
    public string Gender { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn CCCD.")]
    [Display(Name = "Ngày hết hạn")]
    public DateTime? ExpiryDate { get; set; }

    public string PermanentAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn ảnh CCCD mặt trước.")]
    public IFormFile? FrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh CCCD mặt sau.")]
    public IFormFile? BackImage { get; set; }
}

public sealed class KycDrivingLicenseInputViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập số GPLX.")]
    [RegularExpression(@"^[A-Za-z0-9]{8,12}$", ErrorMessage = "Số GPLX phải gồm từ 8 đến 12 ký tự chữ hoặc số, không có khoảng trắng.")]
    [Display(Name = "Số GPLX")]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn hạng GPLX.")]
    [RegularExpression(
        @"^(B|C1|C|D1|D2|D|BE|C1E|CE|D1E|D2E|DE|B1|B2|E|FB2|FC|FD|FE)$",
        ErrorMessage = "Hạng GPLX không hợp lệ đối với xe ô tô.")]
    [StringLength(20, ErrorMessage = "Hạng GPLX không được vượt quá 20 ký tự.")]
    [Display(Name = "Hạng GPLX")]
    public string LicenseClass { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn GPLX.")]
    [Display(Name = "Ngày hết hạn")]
    public DateTime? ExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh GPLX mặt trước.")]
    public IFormFile? FrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh GPLX mặt sau.")]
    public IFormFile? BackImage { get; set; }
}
