using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class AdminVehicleFormViewModel
{
    public int VehicleId { get; set; }

    [Display(Name = "Hãng xe")]
    public int? BrandId { get; set; }

    [Display(Name = "Hãng xe mới")]
    [StringLength(100, ErrorMessage = "Tên hãng xe không được vượt quá 100 ký tự.")]
    public string? NewBrandName { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên xe.")]
    [StringLength(150, ErrorMessage = "Tên xe không được vượt quá 150 ký tự.")]
    [Display(Name = "Tên xe")]
    public string VehicleName { get; set; } = string.Empty;

    [StringLength(100, ErrorMessage = "Phiên bản xe không được vượt quá 100 ký tự.")]
    [Display(Name = "Phiên bản/Model")]
    public string? Model { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập biển số xe.")]
    [StringLength(20, ErrorMessage = "Biển số xe không được vượt quá 20 ký tự.")]
    [Display(Name = "Biển số")]
    public string LicensePlate { get; set; } = string.Empty;

    [Range(1990, 2100, ErrorMessage = "Năm sản xuất không hợp lệ.")]
    [Display(Name = "Năm sản xuất")]
    public int ManufactureYear { get; set; } = DateTime.Today.Year;

    [Range(2, 50, ErrorMessage = "Số chỗ phải từ 2 đến 50.")]
    [Display(Name = "Số chỗ")]
    public int Seats { get; set; } = 5;

    [Required(ErrorMessage = "Vui lòng chọn loại hộp số.")]
    [StringLength(30)]
    [Display(Name = "Hộp số")]
    public string Transmission { get; set; } = "Số tự động";

    [Required(ErrorMessage = "Vui lòng chọn nhiên liệu.")]
    [StringLength(30)]
    [Display(Name = "Nhiên liệu")]
    public string FuelType { get; set; } = "Xăng";

    [StringLength(50)]
    [Display(Name = "Màu xe")]
    public string? Color { get; set; }

    [Range(typeof(decimal), "1", "100000000", ErrorMessage = "Giá thuê phải lớn hơn 0.")]
    [Display(Name = "Giá thuê/ngày")]
    public decimal DailyPrice { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "Số kilomet không hợp lệ.")]
    [Display(Name = "Kilomet hiện tại")]
    public int CurrentMileage { get; set; }

    [Display(Name = "Trạng thái")]
    public VehicleStatus Status { get; set; } = VehicleStatus.Available;

    [StringLength(3000, ErrorMessage = "Mô tả không được vượt quá 3.000 ký tự.")]
    [Display(Name = "Mô tả xe")]
    public string? Description { get; set; }

    [Display(Name = "Ảnh xe")]
    public List<IFormFile> NewImages { get; set; } = new();

    [Display(Name = "Ảnh đại diện")]
    public int? PrimaryImageId { get; set; }

    public List<int> DeleteImageIds { get; set; } = new();
    public IReadOnlyList<AdminBrandOptionViewModel> BrandOptions { get; set; } = Array.Empty<AdminBrandOptionViewModel>();
    public IReadOnlyList<AdminVehicleImageViewModel> ExistingImages { get; set; } = Array.Empty<AdminVehicleImageViewModel>();
}

public sealed class AdminBrandOptionViewModel
{
    public int BrandId { get; init; }
    public string BrandName { get; init; } = string.Empty;
}

public sealed class AdminVehicleImageViewModel
{
    public int VehicleImageId { get; init; }
    public string ImagePath { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public int SortOrder { get; init; }
}
