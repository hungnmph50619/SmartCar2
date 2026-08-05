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
