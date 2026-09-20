using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingReservationExpiryAtomicityTests
{
    [Fact]
    public async Task ConcurrentExpiryRuns_CreateOnlyOneRefundAndOneHoldEvent()
    {
        const string connectionString =
            "Data Source=booking-expiry-race;Mode=Memory;Cache=Shared";

        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();

        var setupOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var setup = new ExpiryTestDbContext(setupOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(new ApplicationUser
            {
                Id = "expiry-customer",
                UserName = "expiry-customer",
                NormalizedUserName = "EXPIRY-CUSTOMER",
                FullName = "Expiry Customer"
            });
            setup.Brands.Add(new Brand
            {
                BrandId = 1,
                BrandName = "Expiry Brand"
            });
            setup.Vehicles.Add(new Vehicle
            {
                VehicleId = 1,
                BrandId = 1,
                VehicleName = "Expiry Car",
                LicensePlate = "30A-EXPIRY",
                ManufactureYear = 2025,
                Seats = 5,
                Transmission = "AT",
                FuelType = "Gasoline",
                DailyPrice = 700_000m,
                Status = VehicleStatus.Available,
                RowVersion = new byte[] { 1 }
            });
            setup.Bookings.Add(new Booking
            {
                BookingId = 901,
                CustomerId = "expiry-customer",
                VehicleId = 1,
                PickupDate = DateTime.UtcNow.AddHours(2),
                ReturnDate = DateTime.UtcNow.AddDays(1),
                DailyPrice = 700_000m,
                NumberOfDays = 1,
                RentalAmount = 700_000m,
                DepositAmount = 300_000m,
                TotalAmount = 1_000_000m,
                Status = BookingStatus.PendingPayment,
                ReservationExpiresAt = DateTime.UtcNow.AddMinutes(-1),
                RowVersion = new byte[] { 1 }
            });
            setup.Payments.AddRange(
                new Payment
                {
                    BookingId = 901,
                    Type = PaymentType.Rental,
                    Amount = 700_000m,
                    Method = SmartCar.Domain.Constants.PaymentMethods.BankQr,
                    Status = PaymentStatus.Paid,
                    PaidAt = DateTime.UtcNow.AddMinutes(-10),
                    TransactionCode = "QR-901"
                },
                new Payment
                {
                    BookingId = 901,
                    Type = PaymentType.Deposit,
                    Amount = 300_000m,
                    Method = SmartCar.Domain.Constants.PaymentMethods.BankQr,
                    Status = PaymentStatus.Paid,
                    PaidAt = DateTime.UtcNow.AddMinutes(-10),
                    TransactionCode = "QR-901"
                });
            await setup.SaveChangesAsync();
        }

        var first = RunExpiryAsync(connectionString);
        var second = RunExpiryAsync(connectionString);

        await Task.WhenAll(first, second);

        await using var verify = new ExpiryTestDbContext(setupOptions);
        var refunds = await verify.Payments
            .Where(item => item.BookingId == 901 && item.Type == PaymentType.Refund)
            .CountAsync();
        var holdEvents = await verify.BookingHoldEvents
            .Where(item => item.BookingId == 901)
            .CountAsync();
        var status = await verify.Bookings
            .Where(item => item.BookingId == 901)
            .Select(item => item.Status)
            .SingleAsync();

        Assert.Equal(1, refunds);
        Assert.Equal(1, holdEvents);
        Assert.Equal(BookingStatus.Expired, status);
    }

    private static async Task RunExpiryAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using var db = new ExpiryTestDbContext(options);
        var implementationType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.BookingReservationPolicy",
            throwOnError: true)!;
        var policy = Activator.CreateInstance(
            implementationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { db },
            culture: null)!;
        var method = implementationType.GetMethod(
            "ExpireStaleReservationsAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var task = (Task)method.Invoke(policy, new object[] { CancellationToken.None })!;
        await task;
    }

    private sealed class ExpiryTestDbContext : ApplicationDbContext
    {
        public ExpiryTestDbContext(DbContextOptions<ApplicationDbContext> options)
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
