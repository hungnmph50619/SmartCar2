using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;

namespace SmartCar.Domain.Entities;

public class Booking
{
    public string? PolicyJson { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public RentalPolicySnapshot Policy => RentalPolicySnapshot.FromJson(PolicyJson);

    public int BookingId { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public int VehicleId { get; set; }
    public DateTime PickupDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public decimal DailyPrice { get; set; }
    public int NumberOfDays { get; set; }
    public decimal RentalAmount { get; set; }
    public decimal DepositAmount { get; set; }

    // Snapshot chính sách giữ cọc tại thời điểm booking được tạo.
    // Admin đổi cấu hình sau đó không làm thay đổi booking cũ.
    public int DepositHoldDaysApplied { get; set; } = DepositHoldPolicy.DefaultDays;

    public decimal AdditionalAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public VehiclePickupMethod PickupMethod { get; set; }
        = VehiclePickupMethod.StorePickup;
    public BookingSource Source { get; set; } = BookingSource.CustomerWeb;
    public bool IsImmediateCounterRental { get; set; }
    public string? DeliveryAddress { get; set; }
    public decimal? DeliveryLatitude { get; set; }
    public decimal? DeliveryLongitude { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PendingConfirmation;

    // Thời điểm hệ thống ngừng giữ lịch xe cho đơn đang chờ xác nhận/chờ thanh toán.
    public DateTime? ReservationExpiresAt { get; set; }

    // Workflow state thật cho bước Staff kiểm tra trước khi Admin duyệt.
    // AuditLog vẫn giữ lịch sử, nhưng không còn bị dùng thay cho trạng thái nghiệp vụ.
    public DateTime? StaffReviewedAt { get; set; }
    public string? StaffReviewedByStaffId { get; set; }

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
