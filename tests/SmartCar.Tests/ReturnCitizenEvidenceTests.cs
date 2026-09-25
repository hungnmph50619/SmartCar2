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

public sealed class ReturnCitizenEvidenceTests
{
    [Theory]
    [InlineData(false, true, "staff-1")]
    [InlineData(true, false, "staff-1")]
    [InlineData(true, true, "staff-2")]
    public async Task CreateAsync_RejectsMissingOrOtherStaffReturnCitizenImages(
        bool includeFront, bool includeBack, string evidenceStaffId)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var faceId = AddEvidence(db, includeFront, includeBack, evidenceStaffId);
        await db.SaveChangesAsync();

        var result = await CreateReturnService(db).CreateAsync(ReturnRequest(faceId));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("CCCD mặt trước + mặt sau", StringComparison.Ordinal));
        Assert.Equal(BookingStatus.Rented, (await db.Bookings.SingleAsync()).Status);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingVerifiedCitizenDocumentEvenWithBothImages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        await db.SaveChangesAsync();
        var faceId = AddEvidence(db, true, true, "staff-1");
        db.CustomerDocuments.RemoveRange(await db.CustomerDocuments.ToListAsync());
        await db.SaveChangesAsync();

        var result = await CreateReturnService(db).CreateAsync(ReturnRequest(faceId));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("Không có CCCD", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(BookingStatus.Rented, (await db.Bookings.SingleAsync()).Status);
    }

    [Fact]
    public async Task CreateAsync_ConsumesBothReturnCitizenImagesForCorrectCustomer()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var faceId = AddEvidence(db, true, true, "staff-1");
        await db.SaveChangesAsync();

        var result = await CreateReturnService(db).CreateAsync(ReturnRequest(faceId));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(BookingStatus.PendingInspection, (await db.Bookings.SingleAsync()).Status);
        Assert.Equal(2, await db.IdentityCaptureSessions.CountAsync(session =>
            (session.Purpose == IdentityCapturePurposes.ReturnCitizenFront ||
             session.Purpose == IdentityCapturePurposes.ReturnCitizenBack) &&
            session.ConsumedAt.HasValue));
    }

    private static async Task<TestDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options;
        var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.Now;
        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1", UserName = "customer-1", NormalizedUserName = "CUSTOMER-1",
            FullName = "Khách thuê"
        });
        db.CustomerDocuments.Add(new CustomerDocument
        {
            CustomerId = "customer-1", DocumentType = DocumentTypes.CitizenId,
            DocumentNumber = "012345678901", ImagePath = "secure/kyc-front.jpg",
            Status = DocumentStatus.Verified, VerifiedAt = DateTime.UtcNow.AddDays(-1)
        });
        db.Brands.Add(new Brand { BrandId = 1, BrandName = "Test Brand" });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 10, BrandId = 1, VehicleName = "Test Car", LicensePlate = "30A-12345",
            ManufactureYear = 2025, Seats = 5, Transmission = "AT", FuelType = "Gasoline",
            DailyPrice = 700_000m, CurrentMileage = 1_000, Status = VehicleStatus.Rented,
            RowVersion = new byte[] { 1 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 701, CustomerId = "customer-1", VehicleId = 10,
            PickupDate = now.AddDays(-1), ReturnDate = now.AddDays(1),
            DailyPrice = 700_000m, NumberOfDays = 2, RentalAmount = 1_400_000m,
            TotalAmount = 1_400_000m, Status = BookingStatus.Rented,
            RowVersion = new byte[] { 1 }
        });
        db.VehicleHandovers.Add(new VehicleHandover
        {
            BookingId = 701, HandoverAt = now.AddHours(-1), Mileage = 1_000,
            FuelLevel = "100%", ImagePaths = "/uploads/handovers/701/signed-handover-test.png",
            IncludedKilometers = 600, ExcessKmFeePerKm = 5_000m,
            LateReturnFeeMultiplier = 1.5m, TrafficFineTerms = "Test",
            DamageCompensationTerms = "Test", PenaltyPolicyAccepted = true,
            CustomerIdentityVerified = true, SignedDocumentVerified = true
        });
        return db;
    }

    private static Guid AddEvidence(ApplicationDbContext db, bool front, bool back, string evidenceStaffId)
    {
        var faceId = Guid.NewGuid();
        db.IdentityCaptureSessions.Add(NewSession(IdentityCapturePurposes.Return, "staff-1", faceId));
        if (front)
            db.IdentityCaptureSessions.Add(NewSession(IdentityCapturePurposes.ReturnCitizenFront, evidenceStaffId));
        if (back)
            db.IdentityCaptureSessions.Add(NewSession(IdentityCapturePurposes.ReturnCitizenBack, evidenceStaffId));
        return faceId;
    }

    private static IdentityCaptureSession NewSession(string purpose, string staffId, Guid? id = null) =>
        new()
        {
            IdentityCaptureSessionId = id ?? Guid.NewGuid(), TokenHash = Guid.NewGuid().ToString("N"),
            Purpose = purpose, TargetCustomerId = "customer-1", BookingId = 701,
            CreatedByUserId = staffId, CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1), ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            ImagePath = $"secure/{purpose}-{Guid.NewGuid():N}.jpg",
            CaptureMethod = purpose == IdentityCapturePurposes.Return
                ? IdentityCaptureMethods.Camera : IdentityCaptureMethods.StaffCounterDocument
        };

    private static CreateReturnRequest ReturnRequest(Guid faceId) =>
        new(701, DateTime.Now, 1_010, "80", "Tốt", "Tốt", "Đủ", false,
            string.Join(';', new[]
            {
                "front-test.png", "rear-test.png", "left-test.png", "right-test.png",
                "interior-test.png", "odometer-test.png", "fuel-test.png"
            }), null, "staff-1", faceId);

    private static IReturnService CreateReturnService(ApplicationDbContext db)
    {
        var type = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.ReturnService", throwOnError: true)!;
        return (IReturnService)Activator.CreateInstance(
            type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { db }, culture: null)!;
    }

    private sealed class TestDbContext : ApplicationDbContext
    {
        public TestDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Vehicle>().Property(item => item.RowVersion).ValueGeneratedNever();
            builder.Entity<Booking>().Property(item => item.RowVersion).ValueGeneratedNever();
        }
    }
}
