using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class Booking
{
    public int BookingId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public int VehicleId { get; set; }
    public DateTime PickupDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public decimal DailyPrice { get; set; }
    public int NumberOfDays { get; set; }
    public decimal RentalAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal AdditionalAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public VehiclePickupMethod PickupMethod { get; set; }
        = VehiclePickupMethod.StorePickup;
    public string? DeliveryAddress { get; set; }
    public decimal? DeliveryLatitude { get; set; }
    public decimal? DeliveryLongitude { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PendingConfirmation;

    // Thời điểm hệ thống ngừng giữ lịch xe cho đơn đang chờ xác nhận/chờ thanh toán.
    // Null khi đơn đã qua giai đoạn giữ chỗ hoặc đang chờ đối soát chuyển khoản.
    public DateTime? ReservationExpiresAt { get; set; }

    public string? CancelReason { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public decimal RefundAmount { get; set; }
    public string? RefundReason { get; set; }
    public DateTime? NoShowMarkedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public Vehicle Vehicle { get; set; } = null!;
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public ICollection<BookingExtension> Extensions { get; set; } = new List<BookingExtension>();
    public ICollection<VehicleIncident> Incidents { get; set; } = new List<VehicleIncident>();
    public VehicleHandover? Handover { get; set; }
    public VehicleReturn? VehicleReturn { get; set; }
    public Review? Review { get; set; }
}
