using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class ExtensionConflictApprovalValidationTests
{
    [Fact]
    public async Task ApproveAsync_ClientConfirmationCannotBypassActiveVehicleConflict()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new TransactionTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.AddRange(
            new ApplicationUser
            {
                Id = "customer-a",
                UserName = "customer-a",
                NormalizedUserName = "CUSTOMER-A",
                FullName = "Customer A"
            },
            new ApplicationUser
            {
                Id = "customer-b",
                UserName = "customer-b",
                NormalizedUserName = "CUSTOMER-B",
                FullName = "Customer B"
            });
        db.Brands.Add(new Brand
        {
            BrandId = 1,
            BrandName = "Test Brand"
        });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 10,
            BrandId = 1,
            VehicleName = "Test Car",
            LicensePlate = "30A-12345",
            ManufactureYear = 2025,
            Seats = 5,
            Transmission = "AT",
            FuelType = "Gasoline",
            DailyPrice = 700_000m,
            Status = VehicleStatus.Rented,
            RowVersion = new byte[] { 1 }
        });

        var originalReturn = new DateTime(2026, 9, 20, 10, 0, 0);
        var requestedReturn = originalReturn.AddHours(4);

        db.Bookings.AddRange(
            CreateBooking(
                701,
                "customer-a",
                originalReturn.AddDays(-1),
                originalReturn,
                BookingStatus.Rented),
            CreateBooking(
                802,
                "customer-b",
                originalReturn.AddHours(2),
                originalReturn.AddDays(1),
                BookingStatus.Paid));
        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 77,
            BookingId = 701,
            OriginalReturnDate = originalReturn,
            RequestedReturnDate = requestedReturn,
            AdditionalDays = 1,
            AdditionalAmount = 500_000m,
            Status = BookingExtensionStatus.Pending,
            CustomerNote = "[FORCE_MAJEURE] Có minh chứng."
        });
        await db.SaveChangesAsync();

        var service = CreateExtensionService(db);
        var result = await service.ApproveAsync(
            77,
            "admin-1",
            confirmConflictHandled: true,
            adminNote: null);

        Assert.False(result.Succeeded);

        var extension = await db.BookingExtensions.SingleAsync(item =>
            item.BookingExtensionId == 77);
        Assert.Equal(BookingExtensionStatus.Pending, extension.Status);
        Assert.DoesNotContain(
            await db.Payments.ToListAsync(),
            item => item.BookingId == 701 && item.Type == PaymentType.Extension);
    }

    private static Booking CreateBooking(
        int bookingId,
        string customerId,
        DateTime pickupDate,
        DateTime returnDate,
        BookingStatus status) =>
        new()
        {
            BookingId = bookingId,
            CustomerId = customerId,
            VehicleId = 10,
            PickupDate = pickupDate,
            ReturnDate = returnDate,
            DailyPrice = 500_000m,
            NumberOfDays = 1,
            RentalAmount = 500_000m,
            DepositAmount = 1_000_000m,
            TotalAmount = 500_000m,
            Status = status,
            RowVersion = new byte[] { 1 }
        };

    private static IExtensionService CreateExtensionService(
        ApplicationDbContext db)
    {
        var implementationType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.ExtensionService",
            throwOnError: true)!;

        return (IExtensionService)Activator.CreateInstance(
            implementationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { db },
            culture: null)!;
    }

    private sealed class TransactionTestDbContext : ApplicationDbContext
    {
        public TransactionTestDbContext(
            DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Vehicle>()
                .Property(item => item.RowVersion)
                .ValueGeneratedNever();
            builder.Entity<Booking>()
                .Property(item => item.RowVersion)
                .ValueGeneratedNever();
        }
    }
}
