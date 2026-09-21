using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using Xunit;

namespace SmartCar.Tests;

public sealed class OverdueViolationTargetValidationTests
{
    [Fact]
    public async Task Process_RejectsPostedBookingThatIsNotTheNextAffectedBooking()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new TransactionTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.AddRange(
            CreateUser("customer-a", "Customer A"),
            CreateUser("customer-b", "Customer B"),
            CreateUser("customer-c", "Customer C"));
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
            LicensePlate = "30A-98765",
            ManufactureYear = 2025,
            Seats = 5,
            Transmission = "AT",
            FuelType = "Gasoline",
            DailyPrice = 700_000m,
            Status = VehicleStatus.Rented,
            RowVersion = new byte[] { 1 }
        });

        var now = DateTime.Now;
        db.Bookings.AddRange(
            CreateBooking(
                701,
                "customer-a",
                now.AddDays(-2),
                now.AddHours(-2),
                1_000_000m,
                BookingStatus.Rented),
            CreateBooking(
                802,
                "customer-b",
                now.AddHours(-1),
                now.AddDays(1),
                700_000m,
                BookingStatus.Paid),
            CreateBooking(
                803,
                "customer-c",
                now.AddMinutes(-30),
                now.AddDays(2),
                900_000m,
                BookingStatus.Paid));

        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 91,
            BookingId = 701,
            OriginalReturnDate = now.AddHours(-2),
            RequestedReturnDate = now.AddHours(3),
            AdditionalDays = 1,
            AdditionalAmount = 500_000m,
            Status = BookingExtensionStatus.Rejected,
            AdminNote = "Xe đã có đơn kế tiếp.",
            DecidedAt = DateTime.UtcNow.AddHours(-3)
        });
        db.Payments.Add(new Payment
        {
            BookingId = 701,
            Type = PaymentType.Deposit,
            Amount = 500_000m,
            Method = PaymentMethods.BankQr,
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow.AddDays(-2)
        });

        await db.SaveChangesAsync();

        var audit = new AuditServiceStub();
        var controller = new AdminOverdueViolationsController(
            db,
            CreateBookingOperationService(db, audit),
            audit);

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "admin-1") },
            authenticationType: "Test");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        controller.ControllerContext =
            new ControllerContext { HttpContext = httpContext };
        controller.TempData =
            new TempDataDictionary(httpContext, new TempDataProviderStub());

        var result = await controller.Process(
            renterBookingId: 701,
            affectedBookingId: 803,
            violationConfirmed: true,
            default);

        Assert.IsType<RedirectToActionResult>(result);

        db.ChangeTracker.Clear();
        var legitimateNextStatus = await db.Bookings
            .Where(item => item.BookingId == 802)
            .Select(item => item.Status)
            .SingleAsync();
        var forgedTargetStatus = await db.Bookings
            .Where(item => item.BookingId == 803)
            .Select(item => item.Status)
            .SingleAsync();
        var renterLedgerEntries = await db.Payments
            .Where(item =>
                item.BookingId == 701 &&
                (item.Type == PaymentType.OverdueCompensationDebt ||
                 item.Method == PaymentMethods.DepositDeduction))
            .CountAsync();
        var forgedCompensationEntries = await db.Payments
            .Where(item =>
                item.BookingId == 803 &&
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.CompensationRefund)
            .CountAsync();

        Assert.Equal(BookingStatus.Paid, legitimateNextStatus);
        Assert.Equal(BookingStatus.Paid, forgedTargetStatus);
        Assert.Equal(0, renterLedgerEntries);
        Assert.Equal(0, forgedCompensationEntries);
    }

    private static ApplicationUser CreateUser(string id, string fullName) =>
        new()
        {
            Id = id,
            UserName = id,
            NormalizedUserName = id.ToUpperInvariant(),
            FullName = fullName
        };

    private static Booking CreateBooking(
        int bookingId,
        string customerId,
        DateTime pickupDate,
        DateTime returnDate,
        decimal totalAmount,
        BookingStatus status) =>
        new()
        {
            BookingId = bookingId,
            CustomerId = customerId,
            VehicleId = 10,
            PickupDate = pickupDate,
            ReturnDate = returnDate,
            DailyPrice = totalAmount,
            NumberOfDays = 1,
            RentalAmount = totalAmount,
            DepositAmount = 0m,
            TotalAmount = totalAmount,
            Status = status,
            RowVersion = new byte[] { 1 }
        };

    private static IBookingOperationService CreateBookingOperationService(
        ApplicationDbContext db,
        IAuditService audit)
    {
        var implementationType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.BookingOperationService",
            throwOnError: true)!;

        return (IBookingOperationService)Activator.CreateInstance(
            implementationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { db, audit },
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

    private sealed class TempDataProviderStub : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(
            HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
        }
    }
}
