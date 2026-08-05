using SmartCar.Application.Common;
using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Bookings;

public sealed record CreateBookingRequest(
    int VehicleId,
    DateTime PickupDate,
    DateTime ReturnDate);

public class BookingListItemDto
{
    public int BookingId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string? CustomerPhone { get; init; }
    public int VehicleId { get; init; }
    public string VehicleName { get; init; } = string.Empty;
    public string LicensePlate { get; init; } = string.Empty;
    public string? PrimaryImagePath { get; init; }
    public DateTime PickupDate { get; init; }
    public DateTime ReturnDate { get; init; }
    public decimal TotalAmount { get; init; }
    public BookingStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class BookingDetailsDto : BookingListItemDto
{
    public string CustomerId { get; init; } = string.Empty;
    public decimal DailyPrice { get; init; }
    public int NumberOfDays { get; init; }
    public decimal RentalAmount { get; init; }
    public decimal AdditionalAmount { get; init; }
    public string? CancelReason { get; init; }
    public bool HasHandover { get; init; }
    public bool HasReturn { get; init; }
    public bool RentalPaid { get; init; }
    public bool AdditionalChargePaid { get; init; }
    public IReadOnlyList<PaymentSummaryDto> Payments { get; init; } = Array.Empty<PaymentSummaryDto>();
    public IReadOnlyList<ChargeSummaryDto> AdditionalCharges { get; init; } = Array.Empty<ChargeSummaryDto>();
}

public sealed record PaymentSummaryDto(
    int PaymentId,
    PaymentType Type,
    decimal Amount,
    PaymentStatus Status,
    DateTime? PaidAt,
    string? TransactionCode);

public sealed record ChargeSummaryDto(
    int AdditionalChargeId,
    AdditionalChargeType ChargeType,
    string Description,
    decimal Amount);

public sealed class BookingMutationResult
{
    private BookingMutationResult(bool succeeded, int? bookingId, IReadOnlyCollection<string> errors)
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
        new(false, null, errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());
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
    Task<OperationResult> ConfirmAsync(int bookingId, CancellationToken cancellationToken = default);
    Task<OperationResult> RejectAsync(
        int bookingId,
        string reason,
        CancellationToken cancellationToken = default);
    Task<OperationResult> MarkReadyForPickupAsync(
        int bookingId,
        CancellationToken cancellationToken = default);
}
