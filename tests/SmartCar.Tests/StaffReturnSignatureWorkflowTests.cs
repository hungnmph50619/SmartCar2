using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using SmartCar.Web.Services;
using Xunit;

namespace SmartCar.Tests;

public sealed class StaffReturnSignatureWorkflowTests
{
    [Fact]
    public async Task VerifyReturnSigned_DoesNotOverwriteExistingVerifier()
    {
        await using var db = CreateDbContext();
        db.Bookings.Add(new Booking
        {
            BookingId = 901,
            CustomerId = "customer-1",
            VehicleId = 12,
            PickupDate = DateTime.Now.AddDays(-2),
            ReturnDate = DateTime.Now.AddDays(-1),
            DailyPrice = 500_000m,
            NumberOfDays = 1,
            RentalAmount = 500_000m,
            DepositAmount = 3_000_000m,
            TotalAmount = 500_000m,
            Status = BookingStatus.PendingInspection,
            VehicleReturn = new VehicleReturn
            {
                VehicleReturnId = 801,
                ReturnedAt = DateTime.Now.AddMinutes(-20),
                Mileage = 1000,
                FuelLevel = "80%",
                CustomerIdentityVerified = true,
                ImagePaths = "/secure/returns/signed-return-901-page-1.jpg"
            }
        });
        await db.SaveChangesAsync();

        var first = CreateController(db, "staff-first");
        await first.VerifyReturnSigned(901, signedCopyConfirmed: true, default);

        var afterFirst = await db.VehicleReturns.SingleAsync();
        Assert.True(afterFirst.SignedDocumentVerified);
        Assert.Equal("staff-first", afterFirst.SignedDocumentVerifiedByStaffId);
        var verifiedAt = afterFirst.SignedDocumentVerifiedAt;

        var second = CreateController(db, "staff-second");
        await second.VerifyReturnSigned(901, signedCopyConfirmed: true, default);

        var afterSecond = await db.VehicleReturns.SingleAsync();
        Assert.True(afterSecond.SignedDocumentVerified);
        Assert.Equal("staff-first", afterSecond.SignedDocumentVerifiedByStaffId);
        Assert.Equal(verifiedAt, afterSecond.SignedDocumentVerifiedAt);
        Assert.Contains(
            "đã được nhân viên xác minh",
            second.TempData["ErrorMessage"]?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private static StaffController CreateController(
        ApplicationDbContext db,
        string staffId)
    {
        var controller = new StaffController(
            db,
            new BookingServiceStub(),
            new AuditServiceStub(),
            new BankAccountServiceStub());

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, staffId) },
            authenticationType: "Test");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        controller.ControllerContext =
            new ControllerContext { HttpContext = httpContext };
        controller.TempData =
            new TempDataDictionary(
                httpContext,
                new TempDataProviderStub());

        return controller;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"staff-return-signature-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(
                    InMemoryEventId.TransactionIgnoredWarning))
                .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class BookingServiceStub : IBookingService
    {
        public Task<BookingMutationResult> CreateAsync(
            string customerId,
            CreateBookingRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BookingListItemDto>> GetCustomerBookingsAsync(
            string customerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookingDetailsDto?> GetCustomerBookingAsync(
            int bookingId,
            string customerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BookingListItemDto>> GetAdminBookingsAsync(
            BookingStatus? status = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookingDetailsDto?> GetAdminBookingAsync(
            int bookingId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> ConfirmAsync(
            int bookingId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> RejectAsync(
            int bookingId,
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> MarkReadyForPickupAsync(
            int bookingId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BankAccountServiceStub : IUserBankAccountService
    {
        public IReadOnlyList<BankOption> Banks => Array.Empty<BankOption>();

        public Task<UserBankAccountDto?> GetDefaultAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
