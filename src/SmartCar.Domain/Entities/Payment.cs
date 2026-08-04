using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class Payment
{
    public int PaymentId { get; set; }
    public int BookingId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "Mô phỏng";
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public DateTime? PaidAt { get; set; }
    public string? TransactionCode { get; set; }

    public Booking Booking { get; set; } = null!;
}
