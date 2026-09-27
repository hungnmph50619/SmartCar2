using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.Filters;
using Xunit;

namespace SmartCar.Tests;

public sealed class StaffBookingClaimTests
{
    [Fact]
    public async Task OneStaffOwnsBookingUntilReleaseOrExpiry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options;
        await using var db = new ClaimTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1", UserName = "customer-1", NormalizedUserName = "CUSTOMER-1",
            FullName = "Customer"
        });
        db.Brands.Add(new Brand { BrandId = 1, BrandName = "Brand" });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 1, BrandId = 1, VehicleName = "Car", LicensePlate = "30A-12345",
            ManufactureYear = 2025, Seats = 5, Transmission = "AT", FuelType = "Gasoline",
            DailyPrice = 500_000m, RowVersion = new byte[] { 1 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 1, CustomerId = "customer-1", VehicleId = 1,
            PickupDate = DateTime.Now, ReturnDate = DateTime.Now.AddDays(1),
            DailyPrice = 500_000m, NumberOfDays = 1, RentalAmount = 500_000m,
            TotalAmount = 500_000m, Status = BookingStatus.PendingConfirmation,
            RowVersion = new byte[] { 1 }
        });
        await db.SaveChangesAsync();

        var claims = new StaffBookingClaimService(db);
        Assert.True(await claims.TryClaimAsync(1, "staff-a"));
        Assert.False(await claims.TryClaimAsync(1, "staff-b"));
        Assert.False(await claims.IsOwnedAsync(1, "staff-b"));
        Assert.True(await claims.IsOwnedAsync(1, "staff-a"));
        Assert.False(await claims.TryRenewAsync(1, "staff-b"));
        Assert.False(await claims.IsOwnedAsync(1, "staff-b"));
        Assert.True(await claims.TryRenewAsync(1, "staff-a"));
        Assert.False(await claims.TryReleaseAsync(1, "staff-b"));
        Assert.True(await claims.TryReleaseAsync(1, "staff-a"));
        Assert.True(await claims.TryClaimAsync(1, "staff-b"));

        await db.Bookings.Where(booking => booking.BookingId == 1)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(booking => booking.HandlingLeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        Assert.False(await claims.TryRenewAsync(1, "staff-b"));
        Assert.True(await claims.TryClaimAsync(1, "staff-a"));
        Assert.True(await claims.ForceReleaseAsync(1));
        Assert.True(await claims.TryClaimAsync(1, "staff-b"));
    }

    [Fact]
    public async Task StaffMutationFilter_RejectsOtherStaffBeforeActionRuns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options;
        await using var db = new ClaimTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1", UserName = "customer-1", NormalizedUserName = "CUSTOMER-1",
            FullName = "Customer"
        });
        db.Brands.Add(new Brand { BrandId = 1, BrandName = "Brand" });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 1, BrandId = 1, VehicleName = "Car", LicensePlate = "30A-12345",
            ManufactureYear = 2025, Seats = 5, Transmission = "AT", FuelType = "Gasoline",
            DailyPrice = 500_000m, RowVersion = new byte[] { 1 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 1, CustomerId = "customer-1", VehicleId = 1,
            PickupDate = DateTime.Now, ReturnDate = DateTime.Now.AddDays(1),
            DailyPrice = 500_000m, NumberOfDays = 1, RentalAmount = 500_000m,
            TotalAmount = 500_000m, Status = BookingStatus.PendingConfirmation,
            RowVersion = new byte[] { 1 }
        });
        await db.SaveChangesAsync();
        var claims = new StaffBookingClaimService(db);
        Assert.True(await claims.TryClaimAsync(1, "staff-a"));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "staff-b"), new Claim(ClaimTypes.Role, "Staff") },
            "test"));
        var descriptor = new ControllerActionDescriptor
        {
            ControllerName = "Returns", ActionName = "AddCharge"
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var context = new ActionExecutingContext(actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["model"] = new SmartCar.Web.ViewModels.AddChargeViewModel { BookingId = 1 } },
            new object());
        var reachedAction = false;

        await new StaffBookingClaimFilter(claims).OnActionExecutionAsync(context, () =>
        {
            reachedAction = true;
            return Task.FromResult(new ActionExecutedContext(actionContext,
                new List<IFilterMetadata>(), new object()));
        });

        Assert.False(reachedAction);
        Assert.IsType<ConflictObjectResult>(context.Result);

        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "staff-a"), new Claim(ClaimTypes.Role, "Staff") },
            "test"));
        var ownerContext = new ActionExecutingContext(actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["model"] = new SmartCar.Web.ViewModels.AddChargeViewModel { BookingId = 1 } },
            new object());
        reachedAction = false;
        await new StaffBookingClaimFilter(claims).OnActionExecutionAsync(ownerContext, () =>
        {
            reachedAction = true;
            return Task.FromResult(new ActionExecutedContext(actionContext,
                new List<IFilterMetadata>(), new object()));
        });
        Assert.True(reachedAction);
    }

    private sealed class ClaimTestDbContext : ApplicationDbContext
    {
        public ClaimTestDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Vehicle>().Property(item => item.RowVersion).ValueGeneratedNever();
            builder.Entity<Booking>().Property(item => item.RowVersion).ValueGeneratedNever();
        }
    }
}
