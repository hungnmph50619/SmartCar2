using SmartCar.Application.Common;

namespace SmartCar.Application.Features.Reviews;

public sealed record ReviewDto(
    int ReviewId,
    int BookingId,
    int VehicleId,
    string CustomerName,
    int Rating,
    string? Comment,
    DateTime CreatedAt);

public interface IReviewService
{
    Task<IReadOnlyList<ReviewDto>> GetVehicleReviewsAsync(
        int vehicleId,
        CancellationToken cancellationToken = default);
    Task<OperationResult> CreateAsync(
        int bookingId,
        string customerId,
        int rating,
        string? comment,
        CancellationToken cancellationToken = default);
}
