using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using SmartCar.Web.Services;
using Xunit;

namespace SmartCar.Tests;

public sealed class LegacyCompensationRepairTests
{
    [Fact]
    public async Task ApproveRefundBatch_LegacyRepairKeepsNewFundedCompensationWithSameAmount()
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
            CreateUser("customer-b", "Customer B"));
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
            LicensePlate = "30A-24680",
            ManufactureYear = 2025,
            Seats = 5,
            Transmission = "AT",
            FuelType = "Gasoline",
            DailyPrice = 500_000m,
            Status = VehicleStatus.Rented,
            RowVersion = new byte[] { 1 }
        });

        var now = DateTime.Now;
        db.Bookings.AddRange(
            CreateBooking(701, "customer-a", BookingStatus.Rented, now),
            CreateBooking(802, "customer-b", BookingStatus.AwaitingRefund, now));
        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 91,
            BookingId = 701,
            OriginalReturnDate = now.AddHours(-2),
            RequestedReturnDate = now.AddHours(2),
            AdditionalDays = 1,
            AdditionalAmount = 500_000m,
            Status = BookingExtensionStatus.Rejected,
            CustomerNote =
                "[FORCE_MAJEURE]\n" +
                "[NEXT_BOOKING_COMPENSATION]500000|BOOKING:802"
        });

        var legacyAutomaticRefund = new Payment
        {
            BookingId = 802,
            Type = PaymentType.Refund,
            Amount = 500_000m,
            Method = PaymentMethods.CompensationRefund,
            Status = PaymentStatus.AwaitingRefund
        };
        var fundedRefundReference =
            OverdueCompensationLedger.BuildFundedRefundCode(
                999,
                802,
                DateTime.UtcNow);
        var newFundedRefund = new Payment
        {
            BookingId = 802,
            Type = PaymentType.Refund,
            Amount = 500_000m,
            Method = PaymentMethods.CompensationRefund,
            Status = PaymentStatus.AwaitingRefund,
            TransactionCode = fundedRefundReference,
            LedgerReference = fundedRefundReference
        };

        db.Payments.Add(legacyAutomaticRefund);
        await db.SaveChangesAsync();
        db.Payments.Add(newFundedRefund);
        await db.SaveChangesAsync();

        Assert.True(newFundedRefund.PaymentId > legacyAutomaticRefund.PaymentId);

        var audit = new AuditServiceStub();
        var controller = new AdminPaymentsController(
            null!,
            audit,
            db,
            new BankAccountServiceStub());

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

        var result = await controller.ApproveRefundBatch(802, default);

        Assert.IsType<RedirectToActionResult>(result);

        db.ChangeTracker.Clear();
        var legacyStatus = await db.Payments
            .Where(item => item.PaymentId == legacyAutomaticRefund.PaymentId)
            .Select(item => item.Status)
            .SingleAsync();
        var fundedStatus = await db.Payments
            .Where(item => item.PaymentId == newFundedRefund.PaymentId)
            .Select(item => item.Status)
            .SingleAsync();

        Assert.Equal(PaymentStatus.Failed, legacyStatus);
        Assert.Equal(PaymentStatus.RefundApproved, fundedStatus);
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
        BookingStatus status,
        DateTime now) =>
        new()
        {
            BookingId = bookingId,
            CustomerId = customerId,
            VehicleId = 10,
            PickupDate = now.AddDays(-2),
            ReturnDate = now.AddDays(1),
            DailyPrice = 500_000m,
            NumberOfDays = 1,
            RentalAmount = 500_000m,
            DepositAmount = 0m,
            TotalAmount = 500_000m,
            Status = status,
            RowVersion = new byte[] { 1 }
        };

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

    private sealed class BankAccountServiceStub : IUserBankAccountService
    {
        public IReadOnlyList<BankOption> Banks => Array.Empty<BankOption>();

        public Task<UserBankAccountDto?> GetDefaultAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UserBankAccountDto?>(new UserBankAccountDto(
                1,
                userId,
                "VCB",
                "Vietcombank",
                "0123456789",
                "CUSTOMER B",
                true,
                true,
                DateTime.UtcNow.AddDays(-1),
                DateTime.UtcNow.AddDays(-1)));

        public Task SaveDefaultAsync(
            string userId,
            string bankCode,
            string accountNumber,
            string accountHolderName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
