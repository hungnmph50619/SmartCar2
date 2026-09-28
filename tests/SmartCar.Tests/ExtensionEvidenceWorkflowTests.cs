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

public sealed class ExtensionEvidenceWorkflowTests
{
    [Fact]
    public async Task RequestMoreEvidence_RejectsNormalExtension()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection, BookingExtensionStatus.Pending, "Cần thêm một ngày");

        var service = CreateService(db);
        var result = await service.RequestMoreEvidenceAsync(
            77,
            "admin-1",
            "Gửi thêm ảnh");

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => error.Contains("gia hạn thông thường", StringComparison.OrdinalIgnoreCase));

        var extension = await db.BookingExtensions.SingleAsync();
        Assert.Equal(BookingExtensionStatus.Pending, extension.Status);
        Assert.DoesNotContain("[FORCE_MAJEURE]", extension.CustomerNote ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupplementEvidence_RejectsLegacyNormalNeedsEvidenceRecord()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection, BookingExtensionStatus.NeedsEvidence, "Gia hạn thông thường cũ");

        var service = CreateService(db);
        var result = await service.SupplementEvidenceAsync(
            77,
            "customer-1",
            "Ảnh và GPS mới");

        Assert.False(result.Succeeded);
        var extension = await db.BookingExtensions.SingleAsync();
        Assert.Equal(BookingExtensionStatus.NeedsEvidence, extension.Status);
        Assert.DoesNotContain("[FORCE_MAJEURE]", extension.CustomerNote ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupplementEvidence_ForceMajeure_RemainsForceMajeure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(
            connection,
            BookingExtensionStatus.NeedsEvidence,
            "[FORCE_MAJEURE]\n[EVIDENCE]Ảnh cũ\n[NOTE]Xe gặp sự cố");

        var service = CreateService(db);
        var result = await service.SupplementEvidenceAsync(
            77,
            "customer-1",
            "Ảnh mới | Vị trí trực tiếp: 21.000000, 105.800000");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var extension = await db.BookingExtensions.SingleAsync();
        Assert.Equal(BookingExtensionStatus.Pending, extension.Status);
        Assert.Contains("[FORCE_MAJEURE]", extension.CustomerNote ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[EVIDENCE]Ảnh mới", extension.CustomerNote ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[NOTE]Xe gặp sự cố", extension.CustomerNote ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task<TestDbContext> CreateDbAsync(
        SqliteConnection connection,
        BookingExtensionStatus extensionStatus,
        string customerNote)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1",
            UserName = "customer-1",
            NormalizedUserName = "CUSTOMER-1",
            FullName = "Khách thử"
        });
        db.Brands.Add(new Brand
        {
            BrandId = 1,
            BrandName = "SmartCar"
        });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 10,
            BrandId = 1,
            VehicleName = "Xe thử",
            LicensePlate = "30A-12345",
            ManufactureYear = 2025,
            Seats = 5,
            Transmission = "AT",
            FuelType = "Gasoline",
            DailyPrice = 500_000m,
            Status = VehicleStatus.Rented,
            RowVersion = new byte[] { 1 }
        });

        var now = DateTime.Now;
        db.Bookings.Add(new Booking
        {
            BookingId = 701,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = now.AddDays(-1),
            ReturnDate = now.AddDays(1),
            DailyPrice = 500_000m,
            NumberOfDays = 2,
            RentalAmount = 1_000_000m,
            DepositAmount = 3_000_000m,
            TotalAmount = 1_000_000m,
            Status = BookingStatus.Rented,
            RowVersion = new byte[] { 1 }
        });
        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 77,
            BookingId = 701,
            OriginalReturnDate = now.AddDays(1),
            RequestedReturnDate = now.AddDays(2),
            AdditionalDays = 1,
            AdditionalAmount = 500_000m,
            Status = extensionStatus,
            CustomerNote = customerNote,
            RequestedAt = DateTime.UtcNow.AddMinutes(-10)
        });

        await db.SaveChangesAsync();
        return db;
    }

    private static IExtensionService CreateService(ApplicationDbContext db)
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

    private sealed class TestDbContext : ApplicationDbContext
    {
        public TestDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Vehicle>().Property(item => item.RowVersion).ValueGeneratedNever();
            builder.Entity<Booking>().Property(item => item.RowVersion).ValueGeneratedNever();
        }
    }
}
