using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingOperationAmbientTransactionTests
{
    [Fact]
    public async Task CancelByAdmin_JoinsAmbientTransactionInsteadOfOpeningNestedTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser
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
            Status = VehicleStatus.Available,
            RowVersion = new byte[] { 1 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 802,
            CustomerId = "customer-b",
            VehicleId = 10,
            PickupDate = DateTime.Now.AddHours(2),
            ReturnDate = DateTime.Now.AddDays(1),
            DailyPrice = 700_000m,
            NumberOfDays = 1,
            RentalAmount = 700_000m,
            DepositAmount = 0m,
            TotalAmount = 700_000m,
            Status = BookingStatus.Paid,
            RowVersion = new byte[] { 1 }
        });
        await db.SaveChangesAsync();

        var service = CreateBookingOperationService(db);

        await using var outerTransaction =
            await db.Database.BeginTransactionAsync();

        var exception = await Record.ExceptionAsync(async () =>
        {
            var result = await service.CancelByAdminAsync(
                "admin-1",
                new CancelBookingRequest(802, "Xe không thể giao do đơn trước giữ quá hạn."),
                default);

            Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        });

        Assert.Null(exception);

        await outerTransaction.RollbackAsync();
        db.ChangeTracker.Clear();

        var statusAfterRollback = await db.Bookings
            .Where(item => item.BookingId == 802)
            .Select(item => item.Status)
            .SingleAsync();

        Assert.Equal(BookingStatus.Paid, statusAfterRollback);
    }

    private static IBookingOperationService CreateBookingOperationService(
        ApplicationDbContext db)
    {
        var implementationType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.BookingOperationService",
            throwOnError: true)!;

        return (IBookingOperationService)Activator.CreateInstance(
            implementationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { db, new AuditServiceStub() },
            culture: null)!;
    }

    private sealed class AuditServiceStub : IAuditService
    {
        public Task WriteAsync(
            string? userId,
            string action,
            string entityName,
            string entityId,
            string description,
            string? oldValues = null,
            string? newValues = null,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AuditLogDto>> GetRecentAsync(
            int take = 200,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AuditLogSearchResult> SearchAsync(
            AuditLogQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AuditLogDto?> GetByIdAsync(
            long auditLogId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
