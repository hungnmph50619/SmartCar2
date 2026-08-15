using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.ViewModels;

public sealed class VehicleDocumentViewModel
{
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

    [StringLength(500)]
    public string? Notes { get; set; }
}

public sealed class IncidentCreateViewModel
{
    [Required]
    public int VehicleId { get; set; }
    public int? BookingId { get; set; }
    public IncidentType IncidentType { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    [StringLength(250)]
    public string? Location { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mô tả sự cố.")]
    [StringLength(1500)]
    public string Description { get; set; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal EstimatedCost { get; set; }

    [Range(0, double.MaxValue)]
    public decimal FineAmount { get; set; }

    [Range(0, double.MaxValue)]
    public decimal CustomerLiabilityAmount { get; set; }

    [StringLength(2000)]
    public string? EvidencePaths { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class IncidentResolveViewModel
{
    [Required]
    public int IncidentId { get; set; }

    [Range(0, double.MaxValue)]
    public decimal ActualCost { get; set; }

    [Range(0, double.MaxValue)]
    public decimal FineAmount { get; set; }

    [Range(0, double.MaxValue)]
    public decimal CustomerLiabilityAmount { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public bool RequiresMaintenance { get; set; }
}
