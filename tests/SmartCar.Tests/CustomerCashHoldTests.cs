using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using Xunit;

namespace SmartCar.Tests;

public sealed class CustomerCashHoldTests
{
    [Fact]
    public async Task ChoosingCash_ExtendsHoldToPickupGraceWithoutMarkingMoneyPaid()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateBookingAsync(connection);
        var controller = CreateController(db, "customer-1");

        await controller.ChooseUpfrontMethod(1, "cash", default);

        var booking = await db.Bookings.Include(item => item.Payments).SingleAsync();
        Assert.Equal(
            CounterRentalSchedule.CashHoldExpiresAtUtc(booking.PickupDate, booking.Policy),
            booking.ReservationExpiresAt);
        Assert.All(booking.Payments, payment =>
        {
            Assert.Equal(PaymentMethods.Cash, payment.Method);
            Assert.Equal(PaymentStatus.Pending, payment.Status);
        });
    }

    [Fact]
    public async Task SwitchingBackToQr_RestoresShortPaymentDeadline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateBookingAsync(connection);
        var controller = CreateController(db, "customer-1");

        await controller.ChooseUpfrontMethod(1, "cash", default);
        var before = DateTime.UtcNow;
        await controller.ChooseUpfrontMethod(1, "qr", default);

        var booking = await db.Bookings.Include(item => item.Payments).SingleAsync();
        Assert.InRange(booking.ReservationExpiresAt!.Value,
            before.AddMinutes(booking.Policy.BookingPaymentHoldMinutes),
            DateTime.UtcNow.AddMinutes(booking.Policy.BookingPaymentHoldMinutes));
        Assert.All(booking.Payments, payment => Assert.Equal(PaymentMethods.BankQr, payment.Method));
    }

    [Fact]
    public async Task ChoosingQrRepeatedly_DoesNotRefreshTheSameHold()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateBookingAsync(connection);
        var controller = CreateController(db, "customer-1");

        await controller.ChooseUpfrontMethod(1, "qr", default);
        var firstDeadline = (await db.Bookings.SingleAsync()).ReservationExpiresAt;
        await controller.ChooseUpfrontMethod(1, "qr", default);

        Assert.Equal(firstDeadline, (await db.Bookings.SingleAsync()).ReservationExpiresAt);
    }

    [Fact]
    public async Task AnotherCustomer_CannotChangePaymentChoice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateBookingAsync(connection);
        var controller = CreateController(db, "another-customer");

        var result = await controller.ChooseUpfrontMethod(1, "cash", default);

        Assert.IsType<NotFoundResult>(result);
        Assert.All((await db.Bookings.Include(item => item.Payments).SingleAsync()).Payments,
            payment => Assert.Equal(PaymentMethods.NotSelected, payment.Method));
    }

    [Fact]
    public async Task QrSubmission_NearPickup_DoesNotExtendHoldPastOperationalCutoff()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateBookingAsync(connection);

        var booking = await db.Bookings.Include(item => item.Payments).SingleAsync();
        booking.PickupDate = DateTime.Now.AddMinutes(20);
        booking.ReturnDate = DateTime.Now.AddDays(1);
        booking.PolicyJson = new RentalPolicySnapshot
        {
            NoShowGraceMinutes = 30,
            BookingPaymentHoldMinutes = 30,
            BookingTransferReconciliationHoldMinutes = 120
        }.ToJson();
        await db.SaveChangesAsync();

        var controller = CreateController(db, "customer-1");
        await controller.ChooseUpfrontMethod(1, "cash", default);

        var serviceType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.PaymentService",
            throwOnError: true)!;
        var service = (SmartCar.Application.Features.Payments.IPaymentService)
            Activator.CreateInstance(
                serviceType,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: new object[] { db, new NoopAuditService() },
                culture: null)!;

        var result = await service.SubmitQrPaymentAsync(
            1,
            "customer-1",
            PaymentType.Rental,
            "customer-1");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        booking = await db.Bookings.SingleAsync();
        var cutoff = CounterRentalSchedule.CashHoldExpiresAtUtc(
            booking.PickupDate,
            booking.Policy);

        Assert.True(booking.ReservationExpiresAt.HasValue);
        Assert.True(booking.ReservationExpiresAt.Value <= cutoff);
    }

    [Fact]
    public async Task CounterBooking_UsesStaffPaymentActionsInsteadOfCustomerUpfrontActions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateBookingAsync(connection);
        var booking = await db.Bookings.SingleAsync();
        booking.Source = BookingSource.StaffCounter;
        await db.SaveChangesAsync();
        var controller = CreateController(db, "customer-1");

        await controller.ChooseUpfrontMethod(1, "qr", default);
        await controller.SubmitQr(1, PaymentType.Rental, null, default);

        Assert.All((await db.Bookings.Include(item => item.Payments).SingleAsync()).Payments,
            payment =>
            {
                Assert.Equal(PaymentMethods.NotSelected, payment.Method);
                Assert.Equal(PaymentStatus.Pending, payment.Status);
            });
    }

    private static async Task<ApplicationDbContext> CreateBookingAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options;
        var db = new CashHoldDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser
        {
            Id = "customer-1", UserName = "customer-1", NormalizedUserName = "CUSTOMER-1",
            FullName = "Khách thử nghiệm"
        });
        db.Brands.Add(new Brand { BrandId = 1, BrandName = "Brand" });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 1, BrandId = 1, VehicleName = "Xe thử nghiệm",
            LicensePlate = "30A-12345", ManufactureYear = 2025, Seats = 5,
            Transmission = "AT", FuelType = "Gasoline", DailyPrice = 500_000m,
            RowVersion = new byte[] { 1 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 1, CustomerId = "customer-1", VehicleId = 1,
            PickupDate = DateTime.Now.AddDays(1), ReturnDate = DateTime.Now.AddDays(2),
            DailyPrice = 500_000m, NumberOfDays = 1, RentalAmount = 500_000m,
            DepositAmount = 1_500_000m, TotalAmount = 500_000m,
            Status = BookingStatus.PendingPayment,
            PolicyJson = new RentalPolicySnapshot { NoShowGraceMinutes = 30 }.ToJson(),
            ReservationExpiresAt = DateTime.UtcNow.AddMinutes(30),
            RowVersion = new byte[] { 1 },
            Payments = new List<Payment>
            {
                new() { Type = PaymentType.Rental, Amount = 500_000m },
                new() { Type = PaymentType.Deposit, Amount = 1_500_000m }
            }
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static PaymentsController CreateController(ApplicationDbContext db, string userId)
    {
        var controller = new PaymentsController(null!, db);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, new TempDataProviderStub());
        return controller;
    }

    private sealed class CashHoldDbContext : ApplicationDbContext
    {
        public CashHoldDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Vehicle>().Property(item => item.RowVersion).ValueGeneratedNever();
            builder.Entity<Booking>().Property(item => item.RowVersion).ValueGeneratedNever();
        }
    }

    private sealed class NoopAuditService : IAuditService
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
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
