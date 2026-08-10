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
    [StringLength(150, ErrorMessage = "Họ và tên không được vượt quá 150 ký tự.")]
    public string FullNameOnDocument { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [RegularExpression(@"^[0-9]{12}$", ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày sinh.")]
    public DateTime? DateOfBirth { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn CCCD.")]
    public DateTime? ExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập nơi cư trú trên CCCD.")]
    [StringLength(500, ErrorMessage = "Nơi cư trú không được vượt quá 500 ký tự.")]
    public string PermanentAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn ảnh CCCD mặt trước.")]
    public IFormFile? FrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh CCCD mặt sau.")]
    public IFormFile? BackImage { get; set; }
}

public sealed class KycDrivingLicenseInputViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập số GPLX.")]
    [RegularExpression(@"^[A-Za-z0-9]{4,20}$", ErrorMessage = "Số GPLX chỉ gồm chữ và số, từ 4 đến 20 ký tự, không có khoảng trắng.")]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn hạng GPLX.")]
    [RegularExpression(
        @"^(B|C1|C|D1|D2|D|BE|C1E|CE|D1E|D2E|DE|B1|B2|E|FB2|FC|FD|FE)$",
        ErrorMessage = "Hạng GPLX không hợp lệ đối với xe ô tô.")]
    [StringLength(20, ErrorMessage = "Hạng GPLX không được vượt quá 20 ký tự.")]
    public string LicenseClass { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn GPLX.")]
    public DateTime? ExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh GPLX mặt trước.")]
    public IFormFile? FrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh GPLX mặt sau.")]
    public IFormFile? BackImage { get; set; }
}
