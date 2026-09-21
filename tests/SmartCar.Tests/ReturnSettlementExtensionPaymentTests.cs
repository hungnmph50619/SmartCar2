using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Returns;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class ReturnSettlementExtensionPaymentTests
{
    [Fact]
    public async Task CompleteAsync_CountsPaidExtensionTowardRequiredRentalAmount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new TransactionTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1",
            UserName = "customer-1",
            NormalizedUserName = "CUSTOMER-1",
            FullName = "Customer 1"
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
            Status = VehicleStatus.Inspection,
            RowVersion = new byte[] { 1 }
        });

        var pickup = DateTime.Now.AddDays(-2);
        var originalReturn = DateTime.Now.AddDays(-1);
        var extendedReturn = DateTime.Now.AddHours(-1);

        db.Bookings.Add(new Booking
        {
            BookingId = 701,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = pickup,
            ReturnDate = extendedReturn,
            DailyPrice = 700_000m,
            NumberOfDays = 2,
            RentalAmount = 900_000m,
            DepositAmount = 0m,
            AdditionalAmount = 0m,
            TotalAmount = 900_000m,
            Status = BookingStatus.PendingInspection,
            RowVersion = new byte[] { 1 }
        });

        db.VehicleHandovers.Add(new VehicleHandover
        {
            BookingId = 701,
            HandoverAt = pickup,
            Mileage = 10_000,
            FuelLevel = "100%",
            ImagePaths = "/uploads/handovers/701/signed-handover-test.png",
            IncludedKilometers = 200,
            ExcessKmFeePerKm = 5_000m,
            LateReturnFeeMultiplier = 1.5m,
            TrafficFineTerms = "Test",
            DamageCompensationTerms = "Test",
            PenaltyPolicyAccepted = true,
            CustomerIdentityVerified = true,
            SignedDocumentVerified = true
        });

        db.VehicleReturns.Add(new VehicleReturn
        {
            BookingId = 701,
            ReturnedAt = DateTime.Now,
            Mileage = 10_100,
            FuelLevel = "80%",
            ImagePaths = "/uploads/returns/701/signed-return-test.png",
            AccessoryStatus = "Đủ",
            CustomerIdentityVerified = true,
            SignedDocumentVerified = true
        });

        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 91,
            BookingId = 701,
            OriginalReturnDate = originalReturn,
            RequestedReturnDate = extendedReturn,
            AdditionalDays = 1,
            AdditionalAmount = 200_000m,
            Status = BookingExtensionStatus.Paid,
            PaidAt = DateTime.UtcNow.AddHours(-2)
        });

        db.Payments.AddRange(
            new Payment
            {
                BookingId = 701,
                Type = PaymentType.Rental,
                Amount = 700_000m,
                Method = PaymentMethods.Cash,
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow.AddDays(-2)
            },
            new Payment
            {
                BookingId = 701,
                Type = PaymentType.Extension,
                Amount = 200_000m,
                Method = PaymentMethods.Cash,
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow.AddHours(-2)
            });

        await db.SaveChangesAsync();

        var service = CreateReturnService(db);
        var result = await service.CompleteAsync(
            701,
            requiresMaintenance: false,
            maintenanceNote: null,
            default);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        db.ChangeTracker.Clear();
        var booking = await db.Bookings.SingleAsync(item => item.BookingId == 701);
        Assert.Equal(BookingStatus.Completed, booking.Status);
    }

    private static IReturnService CreateReturnService(ApplicationDbContext db)
    {
        var implementationType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.ReturnService",
            throwOnError: true)!;

        return (IReturnService)Activator.CreateInstance(
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
