using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
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

public sealed class ExtensionCompensationFundingWorkflowTests
{
    [Fact]
    public async Task CancelConflictingBooking_UsesAmbientTransactionAndMarksCompensationAsFunded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new TransactionTestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Users.AddRange(
            new ApplicationUser
            {
                Id = "customer-a",
                UserName = "customer-a",
                NormalizedUserName = "CUSTOMER-A",
                FullName = "Customer A"
            },
            new ApplicationUser
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
            LicensePlate = "30A-54321",
            ManufactureYear = 2025,
            Seats = 5,
            Transmission = "AT",
            FuelType = "Gasoline",
            DailyPrice = 700_000m,
            Status = VehicleStatus.Rented,
            RowVersion = new byte[] { 1 }
        });

        var originalReturn = DateTime.Now.AddHours(1);
        var requestedReturn = originalReturn.AddHours(4);

        db.Bookings.AddRange(
            new Booking
            {
                BookingId = 901,
                CustomerId = "customer-a",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddDays(-1),
                ReturnDate = originalReturn,
                DailyPrice = 500_000m,
                NumberOfDays = 1,
                RentalAmount = 500_000m,
                DepositAmount = 1_000_000m,
                TotalAmount = 500_000m,
                Status = BookingStatus.Rented,
                RowVersion = new byte[] { 1 }
            },
            new Booking
            {
                BookingId = 902,
                CustomerId = "customer-b",
                VehicleId = 10,
                PickupDate = originalReturn.AddHours(2),
                ReturnDate = originalReturn.AddDays(1),
                DailyPrice = 700_000m,
                NumberOfDays = 1,
                RentalAmount = 700_000m,
                DepositAmount = 0m,
                TotalAmount = 700_000m,
                Status = BookingStatus.Paid,
                RowVersion = new byte[] { 1 }
            });

        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 77,
            BookingId = 901,
            OriginalReturnDate = originalReturn,
            RequestedReturnDate = requestedReturn,
            AdditionalDays = 1,
            AdditionalAmount = 500_000m,
            Status = BookingExtensionStatus.Pending,
            CustomerNote = "[FORCE_MAJEURE] Có minh chứng."
        });

        db.Payments.Add(new Payment
        {
            BookingId = 901,
            Type = PaymentType.Deposit,
            Amount = 1_000_000m,
            Method = PaymentMethods.BankQr,
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow.AddDays(-1)
        });

        await db.SaveChangesAsync();

        var bookingOperations = new BookingOperationStub(db);
        var controller = new AdminExtensionCompensationsController(
            null!,
            bookingOperations,
            new AuditServiceStub(),
            db);

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

        var result = await controller.CancelConflictingBooking(
            77,
            customerContacted: true,
            compensationAmount: 300_000m,
            compensationReason: "Chi phí phát sinh do không thể nhận xe.",
            default);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(bookingOperations.SawAmbientTransaction);

        var refund = await db.Payments.SingleAsync(item =>
            item.BookingId == 902 &&
            item.Type == PaymentType.Refund &&
            item.Method == PaymentMethods.CompensationRefund);

        Assert.False(string.IsNullOrWhiteSpace(refund.LedgerReference));
        Assert.True(
            CompensationLedger.IsFundedRefund(
                refund.LedgerReference,
                refund.TransactionCode));
    }

    private sealed class BookingOperationStub : IBookingOperationService
    {
        private readonly ApplicationDbContext _db;
        public bool SawAmbientTransaction { get; private set; }

        public BookingOperationStub(ApplicationDbContext db)
        {
            _db = db;
        }

        public Task<RefundResult> CancelByCustomerAsync(
            string customerId,
            CancelBookingRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RefundResult> CancelByStaffAsync(
            string staffId,
            CancelBookingRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<RefundResult> CancelByAdminAsync(
            string adminId,
            CancelBookingRequest request,
            CancellationToken cancellationToken = default)
        {
            SawAmbientTransaction = _db.Database.CurrentTransaction is not null;
            var booking = await _db.Bookings.SingleAsync(
                item => item.BookingId == request.BookingId,
                cancellationToken);
            booking.Status = BookingStatus.Cancelled;
            await _db.SaveChangesAsync(cancellationToken);
            return RefundResult.Success(0m);
        }

        public Task<OperationResult> MarkNoShowAsync(
            int bookingId,
            string staffId,
            bool customerContacted,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
