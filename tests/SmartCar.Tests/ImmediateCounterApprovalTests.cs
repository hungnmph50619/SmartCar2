using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class ImmediateCounterApprovalTests
{
    [Fact]
    public async Task ApproveAfterPickup_PreservesDurationAndOpensPayment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var before = DateTime.Now;

        var result = await ConfirmAsync(db, 101);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var booking = await db.Bookings.Include(item => item.Payments).SingleAsync();
        Assert.Equal(BookingStatus.PendingPayment, booking.Status);
        Assert.True(booking.IsImmediateCounterRental);
        Assert.InRange(booking.PickupDate, before, DateTime.Now);
        Assert.Equal(TimeSpan.FromDays(1), booking.ReturnDate - booking.PickupDate);
        Assert.Contains(booking.Payments, payment => payment.Type == PaymentType.Rental);
    }

    [Fact]
    public async Task ApproveAfterPickup_RejectsVehicleStillInsideActualTurnaroundBuffer()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);

        var current = await db.Bookings.SingleAsync(item => item.BookingId == 101);
        var previousPickup = DateTime.Now.AddHours(-2);
        db.Bookings.Add(new Booking
        {
            BookingId = 99,
            CustomerId = "customer-1",
            VehicleId = 1,
            PickupDate = previousPickup,
            ReturnDate = DateTime.Now.AddHours(-1),
            DailyPrice = 500_000m,
            NumberOfDays = 1,
            RentalAmount = 500_000m,
            DepositAmount = 1_500_000m,
            TotalAmount = 500_000m,
            Status = BookingStatus.Completed,
            RowVersion = new byte[] { 9 }
        });
        db.VehicleReturns.Add(new VehicleReturn
        {
            BookingId = 99,
            ReturnedAt = DateTime.Now.AddMinutes(-30),
            Mileage = 10_000,
            FuelLevel = "80%",
            AccessoryStatus = "Đủ"
        });
        await db.SaveChangesAsync();

        var result = await ConfirmAsync(db, current.BookingId);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => error.Contains("cần chuẩn bị đến", StringComparison.OrdinalIgnoreCase));
        await db.Entry(current).ReloadAsync();
        Assert.Equal(BookingStatus.PendingConfirmation, current.Status);
        Assert.Empty(await db.Payments.Where(item => item.BookingId == 101).ToListAsync());
    }

    [Fact]
    public async Task ApproveAfterPickup_RejectsNewConflictBeforeCollectingMoney()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection);
        var original = await db.Bookings.SingleAsync();
        db.Bookings.Add(new Booking
        {
            BookingId = 102,
            CustomerId = "customer-1",
            VehicleId = 1,
            PickupDate = original.ReturnDate.AddMinutes(65),
            ReturnDate = original.ReturnDate.AddDays(1),
            Status = BookingStatus.PendingPayment,
            RowVersion = new byte[] { 2 }
        });
        await db.SaveChangesAsync();

        var result = await ConfirmAsync(db, 101);

        Assert.False(result.Succeeded);
        await db.Entry(original).ReloadAsync();
        Assert.Equal(BookingStatus.PendingConfirmation, original.Status);
        Assert.Empty(await db.Payments.Where(item => item.BookingId == 101).ToListAsync());
    }

    private static async Task<ApprovalDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApprovalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1", UserName = "customer-1", FullName = "Khách tại quầy"
        });
        db.Brands.Add(new Brand { BrandId = 1, BrandName = "SmartCar" });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 1, BrandId = 1, VehicleName = "Xe thử",
            LicensePlate = "30A-12345", ManufactureYear = 2025, Seats = 5,
            Transmission = "AT", FuelType = "Gasoline", DailyPrice = 500_000m,
            RowVersion = new byte[] { 1 }
        });
        foreach (var type in new[] { VehicleDocumentType.Registration,
                     VehicleDocumentType.Inspection, VehicleDocumentType.Insurance,
                     VehicleDocumentType.RoadFee })
        {
            db.VehicleDocuments.Add(new VehicleDocument
            {
                VehicleId = 1, DocumentType = type, DocumentNumber = type.ToString(),
                IssuedDate = DateTime.Today.AddDays(-1), ExpiryDate = DateTime.Today.AddMonths(3)
            });
        }
        var pickup = DateTime.Now.AddMinutes(-10);
        db.Bookings.Add(new Booking
        {
            BookingId = 101, CustomerId = "customer-1", VehicleId = 1,
            Source = BookingSource.StaffCounter, IsImmediateCounterRental = true,
            PickupDate = pickup, ReturnDate = pickup.AddDays(1),
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            DailyPrice = 500_000m, NumberOfDays = 1,
            RentalAmount = 500_000m, DepositAmount = 1_500_000m,
            TotalAmount = 500_000m, Status = BookingStatus.PendingConfirmation,
            RowVersion = new byte[] { 1 }
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<OperationResult> ConfirmAsync(ApplicationDbContext db, int bookingId)
    {
        var serviceType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.BookingService", throwOnError: true)!;
        var service = Activator.CreateInstance(serviceType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { db, new DocumentServiceStub() }, culture: null)!;
        var method = serviceType.GetMethod("ConfirmAsync")!;
        return await (Task<OperationResult>)method.Invoke(
            service, new object[] { bookingId, CancellationToken.None })!;
    }

    private sealed class ApprovalDbContext : ApplicationDbContext
    {
        public ApprovalDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Vehicle>().Property(item => item.RowVersion).ValueGeneratedNever();
            builder.Entity<Booking>().Property(item => item.RowVersion).ValueGeneratedNever();
        }
    }

    private sealed class DocumentServiceStub : IDocumentService
    {
        public Task<bool> HasValidRentalDocumentsAsync(string customerId, DateTime rentalDate,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<DocumentDto>> GetCustomerDocumentsAsync(string customerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentDto?> GetDocumentAsync(int documentId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentDto>> GetPendingDocumentsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> SubmitAsync(string customerId, SubmitDocumentRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> SubmitCitizenIdAsync(string customerId, SubmitCitizenIdRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> SubmitDrivingLicenseAsync(string customerId, SubmitDrivingLicenseRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> VerifyAsync(int documentId, string adminId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> RejectAsync(int documentId, string adminId, string reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
