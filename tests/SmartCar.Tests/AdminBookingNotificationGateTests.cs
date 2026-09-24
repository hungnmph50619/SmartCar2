using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Notifications;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class AdminBookingNotificationGateTests
{
    [Fact]
    public async Task UnreviewedBooking_IsNotShownToAdmin_AndLegacyWorkItemIsRemoved()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"admin-booking-notification-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(options);
        db.Roles.Add(new IdentityRole
        {
            Id = "admin-role",
            Name = RoleNames.Admin,
            NormalizedName = RoleNames.Admin.ToUpperInvariant()
        });
        db.UserRoles.Add(new IdentityUserRole<string>
        {
            UserId = "admin-1",
            RoleId = "admin-role"
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 701,
            CustomerId = "customer-1",
            VehicleId = 1,
            Status = BookingStatus.PendingConfirmation
        });
        db.Notifications.Add(new Notification
        {
            UserId = "admin-1",
            Title = "Đơn thuê chờ xử lý|701",
            Message = "Legacy notification created before Staff review."
        });
        await db.SaveChangesAsync();

        var serviceType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.NotificationService", throwOnError: true)!;
        var service = (INotificationService)Activator.CreateInstance(serviceType, db)!;

        var notifications = await service.GetAsync("admin-1");

        Assert.DoesNotContain(notifications, item => item.Title == "Đơn thuê chờ xử lý|701");
        Assert.False(await db.Notifications.AnyAsync(item => item.Title == "Đơn thuê chờ xử lý|701"));
        Assert.Equal(0, await service.GetUnreadCountAsync("admin-1"));

        db.Bookings.Single().StaffReviewedAt = DateTime.UtcNow;
        db.Notifications.Add(new Notification
        {
            UserId = "admin-1",
            Title = "Đơn thuê chờ xử lý|701",
            Message = "Staff reviewed and sent the booking."
        });
        await db.SaveChangesAsync();

        notifications = await service.GetAsync("admin-1");
        Assert.Contains(notifications, item => item.Title == "Đơn thuê chờ xử lý|701" && !item.IsRead);
    }
}
