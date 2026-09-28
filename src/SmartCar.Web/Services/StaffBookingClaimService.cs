using Microsoft.EntityFrameworkCore;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Services;

public sealed record StaffBookingClaimState(
    string? StaffId,
    string? StaffName,
    DateTime? ExpiresAt,
    bool IsActive);

public sealed class StaffBookingClaimService
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    private readonly ApplicationDbContext _dbContext;

    public StaffBookingClaimService(ApplicationDbContext dbContext) =>
        _dbContext = dbContext;

    public async Task<bool> TryClaimAsync(
        int bookingId,
        string staffId,
        CancellationToken cancellationToken = default)
    {
        if (bookingId <= 0 || string.IsNullOrWhiteSpace(staffId))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var until = now.Add(LeaseDuration);

        var updated = await _dbContext.Bookings
            .Where(booking =>
                booking.BookingId == bookingId &&
                (booking.HandlingStaffId == null ||
                 booking.HandlingLeaseExpiresAt == null ||
                 booking.HandlingLeaseExpiresAt <= now ||
                 booking.HandlingStaffId == staffId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(booking => booking.HandlingStaffId, staffId)
                .SetProperty(booking => booking.HandlingLeaseExpiresAt, until),
                cancellationToken);

        return updated == 1;
    }

    public async Task<bool> TryRenewAsync(
        int bookingId,
        string staffId,
        CancellationToken cancellationToken = default)
    {
        if (bookingId <= 0 || string.IsNullOrWhiteSpace(staffId))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var until = now.Add(LeaseDuration);

        return await _dbContext.Bookings
            .Where(booking =>
                booking.BookingId == bookingId &&
                booking.HandlingStaffId == staffId &&
                booking.HandlingLeaseExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(booking => booking.HandlingLeaseExpiresAt, until),
                cancellationToken) == 1;
    }

    public Task<bool> IsOwnedAsync(
        int bookingId,
        string staffId,
        CancellationToken cancellationToken = default)
    {
        if (bookingId <= 0 || string.IsNullOrWhiteSpace(staffId))
        {
            return Task.FromResult(false);
        }

        var now = DateTime.UtcNow;
        return _dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(booking =>
                booking.BookingId == bookingId &&
                booking.HandlingStaffId == staffId &&
                booking.HandlingLeaseExpiresAt > now,
                cancellationToken);
    }

    public async Task<bool> TryReleaseAsync(
        int bookingId,
        string staffId,
        CancellationToken cancellationToken = default)
    {
        if (bookingId <= 0 || string.IsNullOrWhiteSpace(staffId))
        {
            return false;
        }

        return await _dbContext.Bookings
            .Where(booking =>
                booking.BookingId == bookingId &&
                booking.HandlingStaffId == staffId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(booking => booking.HandlingStaffId, (string?)null)
                .SetProperty(booking => booking.HandlingLeaseExpiresAt, (DateTime?)null),
                cancellationToken) == 1;
    }

    public async Task<bool> ForceReleaseAsync(
        int bookingId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Bookings
            .Where(booking =>
                booking.BookingId == bookingId &&
                booking.HandlingStaffId != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(booking => booking.HandlingStaffId, (string?)null)
                .SetProperty(booking => booking.HandlingLeaseExpiresAt, (DateTime?)null),
                cancellationToken) == 1;

    public async Task<StaffBookingClaimState?> GetStateAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new
            {
                item.HandlingStaffId,
                item.HandlingLeaseExpiresAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return null;
        }

        var isActive =
            booking.HandlingStaffId != null &&
            booking.HandlingLeaseExpiresAt > DateTime.UtcNow;

        var staffName = isActive
            ? await _dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == booking.HandlingStaffId)
                .Select(user => user.FullName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new StaffBookingClaimState(
            isActive ? booking.HandlingStaffId : null,
            staffName,
            isActive ? booking.HandlingLeaseExpiresAt : null,
            isActive);
    }
}
