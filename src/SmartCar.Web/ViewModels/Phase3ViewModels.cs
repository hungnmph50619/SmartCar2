using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class VehicleDocumentViewModel
{
    public int? VehicleDocumentId { get; set; }

    [Required]
    public int VehicleId { get; set; }

    [Required]
    public VehicleDocumentType DocumentType { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập số giấy tờ.")]
    [StringLength(100)]
    public string DocumentNumber { get; set; } = string.Empty;

    [Required]
    public DateTime IssuedDate { get; set; } = DateTime.Today;

    public DateTime? ExpiryDate { get; set; }

    [Display(Name = "Ảnh giấy tờ")]
    public IFormFile? ImageFile { get; set; }

    public string? ExistingImagePath { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}

public sealed class IncidentCreateViewModel
{
    [Required]
    public int VehicleId { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Mã đơn liên quan phải lớn hơn 0.")]
    public int? BookingId { get; set; }

    public IncidentType IncidentType { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    [StringLength(250, ErrorMessage = "Địa điểm không được vượt quá 250 ký tự.")]
    public string? Location { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mô tả sự cố.")]
    [StringLength(1500, ErrorMessage = "Mô tả không được vượt quá 1.500 ký tự.")]
    public string Description { get; set; } = string.Empty;

    [Range(0, double.MaxValue, ErrorMessage = "Chi phí ước tính không được âm.")]
    public decimal EstimatedCost { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Tiền phạt chính thức không được âm.")]
    public decimal FineAmount { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Số tiền khách chịu không được âm.")]
    public decimal CustomerLiabilityAmount { get; set; }

    [StringLength(2000, ErrorMessage = "Bằng chứng/thông báo không được vượt quá 2.000 ký tự.")]
    public string? EvidencePaths { get; set; }

    [StringLength(1000, ErrorMessage = "Ghi chú không được vượt quá 1.000 ký tự.")]
    public string? Notes { get; set; }
}

public sealed class IncidentResolveViewModel
{
    [Required]
    public int IncidentId { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Chi phí thực tế không được âm.")]
    public decimal ActualCost { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Tiền phạt chính thức không được âm.")]
    public decimal FineAmount { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Số tiền khách chịu không được âm.")]
    public decimal CustomerLiabilityAmount { get; set; }

    [StringLength(1000, ErrorMessage = "Ghi chú không được vượt quá 1.000 ký tự.")]
    public string? Notes { get; set; }

}
