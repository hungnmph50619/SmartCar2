using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace SmartCar.Web.ViewModels;

public sealed class ReturnEditViewModel
{
    [Range(1, int.MaxValue)]
    public int BookingId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập thời gian trả xe.")]
    public DateTime ReturnedAt { get; set; } = DateTime.Now;

    [Required(ErrorMessage = "Vui lòng nhập số km khi trả xe.")]
    [Range(0, int.MaxValue, ErrorMessage = "Số km phải là số nguyên từ 0 trở lên.")]
    public int? Mileage { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mức nhiên liệu khi trả xe.")]
    [RegularExpression(@"^(?:100|[0-9]{1,2})$", ErrorMessage = "Mức nhiên liệu phải là số từ 0 đến 100.")]
    public string FuelLevel { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng xác nhận phụ kiện khi trả.")]
    [RegularExpression(@"^(Đủ|Thiếu)$", ErrorMessage = "Tình trạng phụ kiện không hợp lệ.")]
    public string AccessoryStatus { get; set; } = "Đủ";

    [StringLength(1000, ErrorMessage = "Nội dung phụ kiện thiếu/mất tối đa 1000 ký tự.")]
    public string? MissingAccessories { get; set; }

    public bool HasDamage { get; set; }

    [StringLength(1500, ErrorMessage = "Ghi chú tối đa 1500 ký tự.")]
    public string? Notes { get; set; }

    public List<string> ExistingImagePaths { get; set; } = new();

    public List<string>? ImagesToDelete { get; set; }

    [Display(Name = "Ảnh hư hỏng bổ sung")]
    public List<IFormFile>? DamageImages { get; set; }

    [Display(Name = "Ảnh mới bổ sung")]
    public List<IFormFile>? NewImages { get; set; }
}
