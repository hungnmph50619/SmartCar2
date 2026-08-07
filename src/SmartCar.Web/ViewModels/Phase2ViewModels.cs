using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.ViewModels;

public sealed class DocumentUploadViewModel
{
    [Required]
    public string DocumentType { get; set; } = DocumentTypes.CitizenId;

    [Required(ErrorMessage = "Vui lòng nhập số giấy tờ.")]
    [StringLength(50)]
    public string DocumentNumber { get; set; } = string.Empty;

    public DateTime? ExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh giấy tờ.")]
    public IFormFile? Image { get; set; }
}

public sealed class CitizenIdVerificationViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên trên CCCD.")]
    [StringLength(150, ErrorMessage = "Họ và tên không được vượt quá 150 ký tự.")]
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
    [StringLength(20)]
    [Display(Name = "Giới tính")]
    public string Gender { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày cấp CCCD.")]
    [Display(Name = "Ngày cấp")]
    public DateTime? IssuedDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn CCCD.")]
    [Display(Name = "Ngày hết hạn")]
    public DateTime? ExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập địa chỉ thường trú.")]
    [StringLength(500, ErrorMessage = "Địa chỉ thường trú không được vượt quá 500 ký tự.")]
    [Display(Name = "Địa chỉ thường trú")]
    public string PermanentAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn ảnh CCCD mặt trước.")]
    [Display(Name = "Ảnh CCCD mặt trước")]
    public IFormFile? FrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh CCCD mặt sau.")]
    [Display(Name = "Ảnh CCCD mặt sau")]
    public IFormFile? BackImage { get; set; }
}

public sealed class DrivingLicenseVerificationViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ và tên trên GPLX.")]
    [StringLength(150, ErrorMessage = "Họ và tên không được vượt quá 150 ký tự.")]
    [Display(Name = "Họ và tên trên GPLX")]
    public string FullNameOnDocument { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số GPLX.")]
    [StringLength(50, MinimumLength = 4, ErrorMessage = "Số GPLX phải có từ 4 đến 50 ký tự.")]
    [Display(Name = "Số GPLX")]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập hạng GPLX.")]
    [StringLength(20, ErrorMessage = "Hạng GPLX không được vượt quá 20 ký tự.")]
    [Display(Name = "Hạng GPLX")]
    public string LicenseClass { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập ngày cấp GPLX.")]
    [Display(Name = "Ngày cấp")]
    public DateTime? IssuedDate { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập ngày hết hạn GPLX.")]
    [Display(Name = "Ngày hết hạn")]
    public DateTime? ExpiryDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh GPLX mặt trước.")]
    [Display(Name = "Ảnh GPLX mặt trước")]
    public IFormFile? FrontImage { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn ảnh GPLX mặt sau.")]
    [Display(Name = "Ảnh GPLX mặt sau")]
    public IFormFile? BackImage { get; set; }
}

public sealed class RejectDocumentViewModel
{
    public int DocumentId { get; set; }

    [Required]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class CancelBookingViewModel
{
    public int BookingId { get; set; }

    [Required]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ExtensionRequestViewModel
{
    public int BookingId { get; set; }

    [Required]
    public DateTime RequestedReturnDate { get; set; }

    [StringLength(500)]
    public string? CustomerNote { get; set; }
}

public sealed class RejectExtensionViewModel
{
    public int ExtensionId { get; set; }

    [Required]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class MaintenanceFormViewModel
{
    [Required]
    public int VehicleId { get; set; }

    [Required]
    public DateTime StartDate { get; set; } = DateTime.Now;

    [Required]
    [StringLength(1000)]
    public string Content { get; set; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal Cost { get; set; }

    [StringLength(200)]
    public string? ServiceProvider { get; set; }

    [Range(0, int.MaxValue)]
    public int Mileage { get; set; }
}

public sealed class CompleteMaintenanceViewModel
{
    public int MaintenanceId { get; set; }

    [Range(0, double.MaxValue)]
    public decimal FinalCost { get; set; }

    [StringLength(1000)]
    public string? CompletionNote { get; set; }
}

public sealed class ReviewFormViewModel
{
    public int BookingId { get; set; }

    [Range(1, 5)]
    public int Rating { get; set; } = 5;

    [StringLength(1000)]
    public string? Comment { get; set; }
}
