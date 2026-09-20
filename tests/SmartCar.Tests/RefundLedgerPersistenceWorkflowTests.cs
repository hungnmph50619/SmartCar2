using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using SmartCar.Web.Services;
using Xunit;

namespace SmartCar.Tests;

public sealed class RefundLedgerPersistenceWorkflowTests
{
    [Fact]
    public async Task CompletedCompensationRefunds_KeepSourceRelationForLaterDebtRelease()
    {
        await using var db = CreateDbContext();

        db.Bookings.AddRange(
            new Booking
            {
                BookingId = 701,
                CustomerId = "customer-a",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddDays(-3),
                ReturnDate = DateTime.Now.AddHours(-2),
                DailyPrice = 500_000m,
                NumberOfDays = 2,
                RentalAmount = 1_000_000m,
                DepositAmount = 200_000m,
                TotalAmount = 1_000_000m,
                Status = BookingStatus.PendingInspection
            },
            new Booking
            {
                BookingId = 802,
                CustomerId = "customer-b",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddDays(-1),
                ReturnDate = DateTime.Now.AddDays(1),
                DailyPrice = 700_000m,
                NumberOfDays = 1,
                RentalAmount = 700_000m,
                DepositAmount = 0m,
                TotalAmount = 700_000m,
                Status = BookingStatus.AwaitingRefund
            });

        var debt = new Payment
        {
            BookingId = 701,
            Type = PaymentType.OverdueCompensationDebt,
            Amount = 500_000m,
            Method = PaymentMethods.BankQr,
            Status = PaymentStatus.AwaitingConfirmation,
            TransactionCode = "OVERDUE-DEBT-701-802-20260920150000"
        };

        db.Payments.AddRange(
            new Payment
            {
                BookingId = 701,
                Type = PaymentType.AdditionalCharge,
                Amount = 200_000m,
                Method = PaymentMethods.DepositDeduction,
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow.AddMinutes(-30),
                TransactionCode = "OVERDUE-COMP-701-802-20260920143000"
            },
            debt,
            new Payment
            {
                BookingId = 802,
                Type = PaymentType.Refund,
                Amount = 200_000m,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.RefundApproved,
                TransactionCode = "OVERDUE-FUNDED-701-802-20260920143100000"
            },
            new Payment
            {
                BookingId = 802,
                Type = PaymentType.Refund,
                Amount = 300_000m,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.RefundApproved,
                TransactionCode = "OVERDUE-FUNDED-999-802-20260920143200000"
            });

        await db.SaveChangesAsync();

        var staff = CreateStaffController(db);
        var completed = await staff.CompleteRefund(
            802,
            "BANK-BATCH-001",
            default);

        Assert.IsType<RedirectToActionResult>(completed);

        var service = CreatePaymentService(db);
        var confirmed = await service.ConfirmQrPaymentAsync(
            debt.PaymentId,
            "staff-1");

        Assert.True(confirmed.Succeeded, string.Join("; ", confirmed.Errors));

        var newRelationRefund = await db.Payments
            .Where(item =>
                item.BookingId == 802 &&
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.CompensationRefund &&
                item.Status == PaymentStatus.AwaitingRefund)
            .SingleAsync();

        Assert.Equal(500_000m, newRelationRefund.Amount);
        Assert.True(
            OverdueCompensationLedger.IsFundedRefundCode(
                newRelationRefund.TransactionCode));
    }

    private static StaffController CreateStaffController(ApplicationDbContext db)
    {
        var controller = new StaffController(
            db,
            null!,
            new AuditServiceStub(),
            new BankAccountServiceStub());

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "staff-1") },
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

    private static IPaymentService CreatePaymentService(ApplicationDbContext db)
    {
        var implementationType = typeof(ApplicationDbContext).Assembly.GetType(
            "SmartCar.Infrastructure.Services.PaymentService",
            throwOnError: true)!;

        return (IPaymentService)Activator.CreateInstance(
            implementationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { db, new AuditServiceStub() },
            culture: null)!;
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(
                    $"refund-ledger-persistence-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(
                    InMemoryEventId.TransactionIgnoredWarning))
                .Options;

        return new ApplicationDbContext(options);
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
                DateTime.UtcNow,
                DateTime.UtcNow));

        public Task SaveDefaultAsync(
            string userId,
            string bankCode,
            string accountNumber,
            string accountHolderName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
