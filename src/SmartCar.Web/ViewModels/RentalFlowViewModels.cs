using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SmartCar.Domain.Enums;
using SmartCar.Web.Validation;

namespace SmartCar.Web.ViewModels;

public sealed class BrandFormViewModel
{
    public int BrandId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên hãng xe.")]
    [StringLength(100)]
    [Display(Name = "Tên hãng xe")]
    public string BrandName { get; set; } = string.Empty;
}

public sealed class VehicleFormViewModel
{
    public int VehicleId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn hãng xe.")]
    [Display(Name = "Hãng xe")]
    public int BrandId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên xe.")]
    [StringLength(150, ErrorMessage = "Tên xe không được vượt quá 150 ký tự.")]
    [Display(Name = "Tên xe")]
    public string VehicleName { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Dòng xe")]
    public string? VehicleModel { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập biển số xe.")]
    [StringLength(20, ErrorMessage = "Biển số xe không được vượt quá 20 ký tự.")]
    [Display(Name = "Biển số")]
    public string LicensePlate { get; set; } = string.Empty;

    [Range(1980, 2100, ErrorMessage = "Năm sản xuất phải từ 1980 đến năm hiện tại.")]
    [Display(Name = "Năm sản xuất")]
    public int ManufactureYear { get; set; } = DateTime.Now.Year;

    [Range(2, 16, ErrorMessage = "Vui lòng chọn số chỗ trong danh sách cho phép.")]
    [Display(Name = "Số chỗ")]
    public int Seats { get; set; } = 5;

    [Required(ErrorMessage = "Vui lòng chọn loại hộp số.")]
    [Display(Name = "Hộp số")]
    public string Transmission { get; set; } = "Tự động";

    [Required(ErrorMessage = "Vui lòng chọn loại nhiên liệu.")]
    [Display(Name = "Nhiên liệu")]
    public string FuelType { get; set; } = "Xăng";

    [Display(Name = "Màu xe")]
    public string? Color { get; set; }

    [Range(1, 100_000_000, ErrorMessage = "Giá thuê/ngày phải từ 1 đến 100.000.000 đồng.")]
    [Display(Name = "Giá thuê/ngày")]
    public decimal DailyPrice { get; set; }

    [Range(0, 2_000_000, ErrorMessage = "Số km hiện tại phải từ 0 đến 2.000.000 km.")]
    [Display(Name = "Số km hiện tại")]
    public int CurrentMileage { get; set; }

    [Display(Name = "Mô tả")]
    public string? Description { get; set; }

    [Display(Name = "Ảnh xe")]
    public List<IFormFile>? Images { get; set; }

    public string? RowVersionBase64 { get; set; }
}

public sealed class VehicleSearchViewModel
{
    [Required]
    [Display(Name = "Ngày giờ nhận xe")]
    public DateTime PickupDate { get; set; } = DateTime.Now.AddDays(1);

    [Required]
    [Display(Name = "Ngày giờ trả xe")]
    public DateTime ReturnDate { get; set; } = DateTime.Now.AddDays(2);

    [Range(1, int.MaxValue, ErrorMessage = "Hãng xe không hợp lệ.")]
    public int? BrandId { get; set; }

    [Range(1, 100, ErrorMessage = "Số chỗ tối thiểu phải từ 1 đến 100.")]
    public int? Seats { get; set; }

    [RegularExpression(@"^(Tự động|Số sàn)$", ErrorMessage = "Hộp số không hợp lệ.")]
    public string? Transmission { get; set; }

    [RegularExpression(@"^(Xăng|Dầu|Điện|Hybrid)$", ErrorMessage = "Loại nhiên liệu không hợp lệ.")]
    public string? FuelType { get; set; }

    [Range(1, 1000000000, ErrorMessage = "Giá thuê tối thiểu phải lớn hơn 0.")]
    public decimal? MinDailyPrice { get; set; }

    [Range(1, 1000000000, ErrorMessage = "Giá thuê tối đa phải lớn hơn 0.")]
    public decimal? MaxDailyPrice { get; set; }

    [Range(1980, 2100, ErrorMessage = "Năm sản xuất tối thiểu không hợp lệ.")]
    public int? MinManufactureYear { get; set; }

    [RegularExpression(@"^(price_asc|price_desc|year_desc)$", ErrorMessage = "Kiểu sắp xếp không hợp lệ.")]
    public string? SortBy { get; set; } = "price_asc";
}

public sealed class CreateBookingViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Xe không hợp lệ.")]
    public int VehicleId { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn thời gian nhận xe.")]
    public DateTime PickupDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn thời gian trả xe.")]
    public DateTime ReturnDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn phương thức nhận xe.")]
    public VehiclePickupMethod PickupMethod { get; set; }
        = VehiclePickupMethod.StorePickup;

    [StringLength(
        500,
        ErrorMessage = "Địa chỉ giao xe tối đa 500 ký tự.")]
    public string? DeliveryAddress { get; set; }

    // Tọa độ nhận từ JavaScript dưới dạng chuỗi dùng dấu chấm
    // (ví dụ: 21.0381298). Controller sẽ parse bằng
    // CultureInfo.InvariantCulture để không phụ thuộc culture Windows/vi-VN.
    public string? DeliveryLatitude { get; set; }

    public string? DeliveryLongitude { get; set; }
}

public sealed class RejectBookingViewModel
{
    public int BookingId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập lý do từ chối.")]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class HandoverViewModel
{
    [Range(1, int.MaxValue)]
    public int BookingId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập thời gian giao xe.")]
    public DateTime HandoverAt { get; set; } = DateTime.Now;

    [Range(0, int.MaxValue, ErrorMessage = "Số km không hợp lệ.")]
    public int Mileage { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mức nhiên liệu khi giao xe.")]
    [StringLength(30)]
    public string FuelLevel { get; set; } = string.Empty;

    [StringLength(1500)]
    public string? ExteriorCondition { get; set; }

    [StringLength(1500)]
    public string? InteriorCondition { get; set; }

    [StringLength(1000)]
    public string? Accessories { get; set; }

    // Ảnh chứng cứ bắt buộc. Controller lưu tên file có tiền tố để tra cứu/đối chiếu.
    public IFormFile? FrontImage { get; set; }
    public IFormFile? RearImage { get; set; }
    public IFormFile? LeftImage { get; set; }
    public IFormFile? RightImage { get; set; }
    public IFormFile? InteriorImage { get; set; }
    public IFormFile? OdometerImage { get; set; }
    public IFormFile? FuelImage { get; set; }

    [Display(Name = "Ảnh khác")]
    public List<IFormFile> Images { get; set; } = new();

    [Range(0, int.MaxValue)]
    public int IncludedKilometers { get; set; }

    [Range(1, 1000000, ErrorMessage = "Phí vượt km phải lớn hơn 0.")]
    public decimal ExcessKmFeePerKm { get; set; }

    [Range(1, 10, ErrorMessage = "Hệ số phí trả muộn không hợp lệ.")]
    public decimal LateReturnFeeMultiplier { get; set; }

    [Required]
    [StringLength(1500)]
    public string TrafficFineTerms { get; set; } = string.Empty;

    [Required]
    [StringLength(1500)]
    public string DamageCompensationTerms { get; set; } = string.Empty;

    [MustBeTrue(
        ErrorMessage =
            "Cần xác nhận đã thông báo và khách đã đồng ý chính sách phí/phạt trước khi giao xe.")]
    public bool PenaltyPolicyAccepted { get; set; }

    [StringLength(1500)]
    public string? Notes { get; set; }
}

public sealed class ReturnViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Đơn thuê không hợp lệ.")]
    public int BookingId { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập thời gian trả xe.")]
    public DateTime ReturnedAt { get; set; } = DateTime.Now;

    [Range(0, int.MaxValue)]
    public int Mileage { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mức nhiên liệu khi trả xe.")]
    [StringLength(30, ErrorMessage = "Mức nhiên liệu tối đa 30 ký tự.")]
    public string FuelLevel { get; set; } = string.Empty;

    [StringLength(1500, ErrorMessage = "Mô tả ngoại thất tối đa 1500 ký tự.")]
    public string? ExteriorCondition { get; set; }

    [StringLength(1500, ErrorMessage = "Mô tả nội thất tối đa 1500 ký tự.")]
    public string? InteriorCondition { get; set; }

    public bool HasDamage { get; set; }

    // Ảnh chứng cứ trả xe theo cùng vị trí với lúc giao để đối chiếu trực tiếp.
    public IFormFile? FrontImage { get; set; }
    public IFormFile? RearImage { get; set; }
    public IFormFile? LeftImage { get; set; }
    public IFormFile? RightImage { get; set; }
    public IFormFile? InteriorImage { get; set; }
    public IFormFile? OdometerImage { get; set; }
    public IFormFile? FuelImage { get; set; }

    [Display(Name = "Ảnh hư hỏng")]
    public List<IFormFile> DamageImages { get; set; } = new();

    [Display(Name = "Ảnh khác")]
    public List<IFormFile> Images { get; set; } = new();

    [StringLength(1500, ErrorMessage = "Ghi chú tối đa 1500 ký tự.")]
    public string? Notes { get; set; }
}

public sealed class AddChargeViewModel
{
    public int BookingId { get; set; }

    [Required]
    public AdditionalChargeType ChargeType { get; set; } = AdditionalChargeType.Other;

    [Required]
    [StringLength(250)]
    public string Description { get; set; } = string.Empty;

    [Range(1, double.MaxValue)]
    public decimal Amount { get; set; }
}

public sealed class CompleteBookingViewModel
{
    public int BookingId { get; set; }
    public bool RequiresMaintenance { get; set; }
    public string? MaintenanceNote { get; set; }
}
