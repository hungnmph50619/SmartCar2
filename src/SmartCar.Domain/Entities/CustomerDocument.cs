using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class CustomerDocument
{
    public int CustomerDocumentId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? ExpiryDate { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? RejectionReason { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
