using SmartCar.Domain.Enums;
using SmartCar.Domain.Constants;

namespace SmartCar.Domain.Entities;

public class Payment
{
    public int PaymentId { get; set; }
    public int BookingId { get; set; }

    // Chỉ dùng với khoản phải thu phạt/vi phạm để truy vết đúng hồ sơ vi phạm.
    public int? VehicleIncidentId { get; set; }

    public PaymentType Type { get; set; } = PaymentType.Rental;
    public decimal Amount { get; set; }
    public string Method { get; set; } = PaymentMethods.NotSelected;
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public DateTime? PaidAt { get; set; }
    public string? TransactionCode { get; set; }

    public Booking Booking { get; set; } = null!;
    public VehicleIncident? VehicleIncident { get; set; }
}
