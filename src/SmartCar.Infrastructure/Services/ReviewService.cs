using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Reviews;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class ReviewService : IReviewService
{
    private readonly ApplicationDbContext _dbContext;

    public ReviewService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ReviewDto>> GetVehicleReviewsAsync(
        int vehicleId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Reviews
            .AsNoTracking()
            .Where(review => review.Booking.VehicleId == vehicleId)
            .OrderByDescending(review => review.CreatedAt)
            .Select(review => new ReviewDto(
                review.ReviewId,
                review.BookingId,
                review.Booking.VehicleId,
                _dbContext.Users
                    .Where(user => user.Id == review.Booking.CustomerId)
                    .Select(user => user.FullName)
                    .FirstOrDefault() ?? "Khách hàng SmartCar",
                review.Rating,
                review.Comment,
                review.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<OperationResult> CreateAsync(
        int bookingId,
        string customerId,
        int rating,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        if (rating is < 1 or > 5)
        {
            return OperationResult.Failure("Điểm đánh giá phải từ 1 đến 5 sao.");
        }

        var booking = await _dbContext.Bookings
            .Include(item => item.Review)
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (booking.Status != BookingStatus.Completed)
        {
            return OperationResult.Failure("Chỉ đơn đã hoàn tất mới được đánh giá.");
        }

        if (booking.Review is not null)
        {
            return OperationResult.Failure("Đơn thuê này đã được đánh giá.");
        }

        booking.Review = new Review
        {
            Rating = rating,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }
}
