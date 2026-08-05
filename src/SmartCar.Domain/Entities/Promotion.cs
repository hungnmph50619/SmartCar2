using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class Promotion
{
    public int PromotionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PromotionType PromotionType { get; set; }
    public decimal Value { get; set; }
    public decimal? MaximumDiscount { get; set; }
    public decimal MinimumRentalAmount { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public int? UsageLimit { get; set; }
    public int UsedCount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
