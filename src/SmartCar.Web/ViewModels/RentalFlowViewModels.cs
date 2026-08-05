using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SmartCar.Domain.Enums;

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

    [Required]
    [Display(Name = "Hãng xe")]
    public int BrandId { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "Tên xe")]
    public string VehicleName { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Dòng xe")]
    public string? VehicleModel { get; set; }

    [Required]
    [StringLength(20)]
    [Display(Name = "Biển số")]
    public string LicensePlate { get; set; } = string.Empty;

    [Range(1980, 2100)]
    [Display(Name = "Năm sản xuất")]
    public int ManufactureYear { get; set; } = DateTime.Now.Year;

    [Range(1, 100)]
    [Display(Name = "Số chỗ")]
    public int Seats { get; set; } = 5;

    [Required]
    [Display(Name = "Hộp số")]
    public string Transmission { get; set; } = "Tự động";

    [Required]
    [Display(Name = "Nhiên liệu")]
    public string FuelType { get; set; } = "Xăng";

    [Display(Name = "Màu xe")]
    public string? Color { get; set; }

    [Range(1, double.MaxValue)]
    [Display(Name = "Giá thuê/ngày")]
    public decimal DailyPrice { get; set; }

    [Range(0, int.MaxValue)]
    [Display(Name = "Số km hiện tại")]
    public int CurrentMileage { get; set; }

    [Display(Name = "Mô tả")]
    public string? Description { get; set; }

    [Display(Name = "Ảnh xe")]
    public List<IFormFile> Images { get; set; } = new();

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

    public int? BrandId { get; set; }
    public int? Seats { get; set; }
    public string? Transmission { get; set; }
    public decimal? MaxDailyPrice { get; set; }
}

public sealed class CreateBookingViewModel
{
    public int VehicleId { get; set; }
    public DateTime PickupDate { get; set; }
    public DateTime ReturnDate { get; set; }
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
    public int BookingId { get; set; }
    public DateTime HandoverAt { get; set; } = DateTime.Now;

    [Range(0, int.MaxValue)]
    public int Mileage { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mức nhiên liệu khi giao xe.")]
    public string FuelLevel { get; set; } = string.Empty;
    public string? ExteriorCondition { get; set; }
    public string? InteriorCondition { get; set; }
    public string? Accessories { get; set; }

    [Display(Name = "Ảnh bàn giao")]
    public List<IFormFile> Images { get; set; } = new();

    public string? Notes { get; set; }
}

public sealed class ReturnViewModel
{
    public int BookingId { get; set; }
    public DateTime ReturnedAt { get; set; } = DateTime.Now;

    [Range(0, int.MaxValue)]
    public int Mileage { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mức nhiên liệu khi trả xe.")]
    public string FuelLevel { get; set; } = string.Empty;
    public string? ExteriorCondition { get; set; }
    public string? InteriorCondition { get; set; }
    public bool HasDamage { get; set; }

    [Display(Name = "Ảnh khi trả xe")]
    public List<IFormFile> Images { get; set; } = new();

    public string? Notes { get; set; }
}

public sealed class AddChargeViewModel
{
    public int BookingId { get; set; }
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
