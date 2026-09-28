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

public sealed class ReturnCreationChronologyTests
{
    [Fact]
    public async Task CreateAsync_RejectsReturnBeforeHandoverTime()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new TransactionTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.Now;
        var faceSessionId = Guid.NewGuid();

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
            CurrentMileage = 1_000,
            Status = VehicleStatus.Rented,
            RowVersion = new byte[] { 1 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 701,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = now.AddDays(-1),
            ReturnDate = now.AddDays(1),
            DailyPrice = 700_000m,
            NumberOfDays = 2,
            RentalAmount = 1_400_000m,
            DepositAmount = 0m,
            AdditionalAmount = 0m,
            TotalAmount = 1_400_000m,
            Status = BookingStatus.Rented,
            RowVersion = new byte[] { 1 }
        });
        db.VehicleHandovers.Add(new VehicleHandover
        {
            BookingId = 701,
            HandoverAt = now.AddHours(1),
            Mileage = 1_000,
            FuelLevel = "100%",
            ImagePaths = "/uploads/handovers/701/signed-handover-test.png",
            IncludedKilometers = 600,
            ExcessKmFeePerKm = 5_000m,
            LateReturnFeeMultiplier = 1.5m,
            TrafficFineTerms = "Test",
            DamageCompensationTerms = "Test",
            PenaltyPolicyAccepted = true,
            CustomerIdentityVerified = true,
            SignedDocumentVerified = true
        });
        db.IdentityCaptureSessions.Add(new IdentityCaptureSession
        {
            IdentityCaptureSessionId = faceSessionId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Purpose = IdentityCapturePurposes.Return,
            TargetCustomerId = "customer-1",
            BookingId = 701,
            CreatedByUserId = "staff-1",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            ExpiresAt = DateTime.UtcNow.AddMinutes(8),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            ImagePath = "/secure-documents/customer/return-face.jpg",
            CaptureMethod = IdentityCaptureMethods.Camera
        });
        await db.SaveChangesAsync();

        var result = await CreateReturnService(db).CreateAsync(
            new CreateReturnRequest(
                BookingId: 701,
                ReturnedAt: now,
                Mileage: 1_010,
                FuelLevel: "80",
                ExteriorCondition: "Tốt",
                InteriorCondition: "Tốt",
                AccessoryStatus: "Đủ",
                HasDamage: false,
                ImagePaths: string.Join(
                    ';',
                    new[]
                    {
                        "/uploads/returns/701/front-test.png",
                        "/uploads/returns/701/rear-test.png",
                        "/uploads/returns/701/left-test.png",
                        "/uploads/returns/701/right-test.png",
                        "/uploads/returns/701/interior-test.png",
                        "/uploads/returns/701/odometer-test.png",
                        "/uploads/returns/701/fuel-test.png"
                    }),
                Notes: null,
                IdentityVerifiedByStaffId: "staff-1",
                IdentityFaceSessionId: faceSessionId),
            default);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "trước thời gian giao xe",
                StringComparison.OrdinalIgnoreCase));
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
