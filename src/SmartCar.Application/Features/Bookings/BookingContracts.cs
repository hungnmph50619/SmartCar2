using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Bookings;

public sealed record CreateBookingRequest(
    int VehicleId,
    DateTime PickupDate,
    DateTime ReturnDate,
    VehiclePickupMethod PickupMethod,
    string? DeliveryAddress,
    decimal? DeliveryLatitude,
    decimal? DeliveryLongitude,
    string? PolicyVersion = null,
    bool IsImmediateCounterRental = false);

public class BookingListItemDto
{
    public int BookingId { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string? CustomerPhone { get; init; }
    public int VehicleId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public string? PrimaryImagePath { get; init; }
    public DateTime PickupDate { get; init; }
    public DateTime ReturnDate { get; init; }

    // Tiền thuê + phí giao + phụ phí. KHÔNG bao gồm tiền cọc.
    public decimal TotalAmount { get; init; }

    // Tiền cọc tách riêng.
    public decimal DepositAmount { get; init; }

    // Số tiền tổng cộng nếu tính cả tiền cọc.
    public decimal GrandTotalAmount => TotalAmount + DepositAmount;

    public BookingStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public VehiclePickupMethod PickupMethod { get; init; }
    public string? DeliveryAddress { get; init; }
}

public sealed class BookingDetailsDto : BookingListItemDto
{
    public string? PolicyJson { get; init; }
    public SmartCar.Domain.Constants.RentalPolicySnapshot Policy => SmartCar.Domain.Constants.RentalPolicySnapshot.FromJson(PolicyJson);
    public decimal DailyPrice { get; init; }
    public int NumberOfDays { get; init; }

    // RentalAmount chỉ bao gồm tiền thuê ban đầu + các lần gia hạn đã thanh toán/áp dụng.
    public decimal RentalAmount { get; init; }

    // Tiền thuê ban đầu. Gia hạn mới được duyệt nhưng chưa thanh toán chưa làm thay đổi RentalAmount.
    public decimal InitialRentalAmount =>
        Math.Max(
            0,
            RentalAmount -
            Extensions
                .Where(extension => extension.Status == BookingExtensionStatus.Paid)
                .Sum(extension => extension.AdditionalAmount));

    // TotalAmount = RentalAmount + DeliveryFee + AdditionalAmount.
    public decimal DeliveryFee =>
        Math.Max(
            0,
            TotalAmount
            - RentalAmount
            - AdditionalAmount);

    // Tiền cần thanh toán trước khi nhận xe.
    public decimal UpfrontAmount =>
        InitialRentalAmount
        + DeliveryFee
        + DepositAmount;

    public decimal AdditionalAmount { get; init; }

    // Khoản đã đối soát từ tiền cọc đã nộp trước đó; không phải tiền khách chuyển thêm.
    public decimal DepositDeductionAmount { get; init; }

    public decimal? DeliveryLatitude { get; init; }
    public decimal? DeliveryLongitude { get; init; }
    public string? CancelReason { get; init; }
    public string? CancelledBy { get; init; }
    public DateTime? CancelledAt { get; init; }
    public decimal RefundAmount { get; init; }
    public string? RefundReason { get; init; }
    public DateTime? NoShowMarkedAt { get; init; }
    public bool HasHandover { get; init; }
    public bool HasReturn { get; init; }
    public bool HasReview { get; init; }
    public bool RentalPaid { get; init; }
    public bool DepositPaid { get; init; }
    public bool UpfrontPaid => RentalPaid && DepositPaid;
    public HandoverDetailsDto? Handover { get; init; }
    public bool AdditionalChargePaid { get; init; }
    public bool ExtensionPaid { get; init; }

    public IReadOnlyList<PaymentSummaryDto> Payments { get; init; }
        = Array.Empty<PaymentSummaryDto>();

    public IReadOnlyList<ChargeSummaryDto> AdditionalCharges { get; init; }
        = Array.Empty<ChargeSummaryDto>();

    public IReadOnlyList<ExtensionSummaryDto> Extensions { get; init; }
        = Array.Empty<ExtensionSummaryDto>();
}

public sealed record HandoverDetailsDto(
    DateTime HandoverAt,
    int Mileage,
    string FuelLevel,
    string? ExteriorCondition,
    string? InteriorCondition,
    string? Accessories,
    int IncludedKilometers,
    decimal ExcessKmFeePerKm,
    decimal LateReturnFeeMultiplier,
    string TrafficFineTerms,
    string DamageCompensationTerms,
    bool PenaltyPolicyAccepted,
    string? Notes,
    IReadOnlyList<string> ImagePaths);

public sealed record PaymentSummaryDto(
    int PaymentId,
    PaymentType Type,
    decimal Amount,
    string Method,
    PaymentStatus Status,
    DateTime? PaidAt,
    string? TransactionCode);

public sealed record ChargeSummaryDto(
    int AdditionalChargeId,
    AdditionalChargeType ChargeType,
    string Description,
    decimal Amount);

public sealed record ExtensionSummaryDto(
    int BookingExtensionId,
    DateTime OriginalReturnDate,
    DateTime RequestedReturnDate,
    int AdditionalDays,
    decimal AdditionalAmount,
    BookingExtensionStatus Status,
    string? CustomerNote,
    string? AdminNote);

public sealed class BookingMutationResult
{
    private BookingMutationResult(
        bool succeeded,
        int? bookingId,
        IReadOnlyCollection<string> errors)
    {
        Succeeded = succeeded;
        BookingId = bookingId;
        Errors = errors;
    }

    public bool Succeeded { get; }
    public int? BookingId { get; }
    public IReadOnlyCollection<string> Errors { get; }

    public static BookingMutationResult Success(int bookingId) =>
        new(true, bookingId, Array.Empty<string>());

    public static BookingMutationResult Failure(params string[] errors) =>
        new(
            false,
            null,
            errors
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .ToArray());
}

public interface IBookingService
{
    Task<BookingMutationResult> CreateAsync(
        string customerId,
        CreateBookingRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookingListItemDto>> GetCustomerBookingsAsync(
        string customerId,
        CancellationToken cancellationToken = default);

    Task<BookingDetailsDto?> GetCustomerBookingAsync(
        int bookingId,
        string customerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookingListItemDto>> GetAdminBookingsAsync(
        BookingStatus? status = null,
        CancellationToken cancellationToken = default);

    Task<BookingDetailsDto?> GetAdminBookingAsync(
        int bookingId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ConfirmAsync(
        int bookingId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RejectAsync(
        int bookingId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<OperationResult> MarkReadyForPickupAsync(
        int bookingId,
        CancellationToken cancellationToken = default);
}
