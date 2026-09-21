using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class OverdueCompensationQrWorkflowTests
{
    [Fact]
    public async Task ConfirmQrPayment_ReleasesOnlyFundedDebtToAffectedBooking()
    {
        await using var db = CreateDbContext();

        db.Bookings.AddRange(
            new Booking
            {
                BookingId = 301,
                CustomerId = "customer-a",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddDays(-2),
                ReturnDate = DateTime.Now.AddHours(-1),
                DailyPrice = 500_000m,
                NumberOfDays = 2,
                RentalAmount = 1_000_000m,
                DepositAmount = 0m,
                TotalAmount = 1_000_000m,
                Status = BookingStatus.PendingInspection
            },
            new Booking
            {
                BookingId = 402,
                CustomerId = "customer-b",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddHours(-1),
                ReturnDate = DateTime.Now.AddDays(1),
                DailyPrice = 700_000m,
                NumberOfDays = 1,
                RentalAmount = 700_000m,
                DepositAmount = 0m,
                TotalAmount = 700_000m,
                Status = BookingStatus.Cancelled
            });

        var debt = new Payment
        {
            BookingId = 301,
            Type = PaymentType.OverdueCompensationDebt,
            Amount = 700_000m,
            Method = PaymentMethods.BankQr,
            Status = PaymentStatus.AwaitingConfirmation,
            TransactionCode = "OVERDUE-DEBT-301-402-20260920130000"
        };
        db.Payments.Add(debt);
        await db.SaveChangesAsync();

        var service = CreatePaymentService(db);
        var result = await service.ConfirmQrPaymentAsync(
            debt.PaymentId,
            "staff-1");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        var paidDebt = await db.Payments.SingleAsync(item =>
            item.BookingId == 301 &&
            item.Type == PaymentType.OverdueCompensationDebt);
        Assert.Equal(PaymentStatus.Paid, paidDebt.Status);
        Assert.Equal(
            "OVERDUE-DEBT-301-402-20260920130000",
            paidDebt.TransactionCode);

        var compensationRefund = await db.Payments.SingleAsync(item =>
            item.BookingId == 402 &&
            item.Type == PaymentType.Refund &&
            item.Method == PaymentMethods.CompensationRefund);

        Assert.Equal(700_000m, compensationRefund.Amount);
        Assert.Equal(PaymentStatus.AwaitingRefund, compensationRefund.Status);
        Assert.True(
            OverdueCompensationLedger.IsFundedRefundCode(
                compensationRefund.TransactionCode));
    }


    [Fact]
    public async Task ConfirmQrPayment_DoesNotCountFundedCompensationFromDifferentSourceRelation()
    {
        await using var db = CreateDbContext();

        db.Bookings.AddRange(
            new Booking
            {
                BookingId = 501,
                CustomerId = "customer-a",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddDays(-2),
                ReturnDate = DateTime.Now.AddHours(-1),
                DailyPrice = 500_000m,
                NumberOfDays = 2,
                RentalAmount = 1_000_000m,
                DepositAmount = 0m,
                TotalAmount = 1_000_000m,
                Status = BookingStatus.PendingInspection
            },
            new Booking
            {
                BookingId = 602,
                CustomerId = "customer-b",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddHours(-1),
                ReturnDate = DateTime.Now.AddDays(1),
                DailyPrice = 700_000m,
                NumberOfDays = 1,
                RentalAmount = 700_000m,
                DepositAmount = 0m,
                TotalAmount = 700_000m,
                Status = BookingStatus.Cancelled
            });

        var debt = new Payment
        {
            BookingId = 501,
            Type = PaymentType.OverdueCompensationDebt,
            Amount = 700_000m,
            Method = PaymentMethods.BankQr,
            Status = PaymentStatus.AwaitingConfirmation,
            TransactionCode = "OVERDUE-DEBT-501-602-20260920140000"
        };
        db.Payments.AddRange(
            debt,
            new Payment
            {
                BookingId = 602,
                Type = PaymentType.Refund,
                Amount = 200_000m,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.AwaitingRefund,
                TransactionCode = "OVERDUE-FUNDED-999-602-20260920130000000"
            });
        await db.SaveChangesAsync();

        var service = CreatePaymentService(db);
        var result = await service.ConfirmQrPaymentAsync(
            debt.PaymentId,
            "staff-1");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        var refunds = await db.Payments
            .Where(item =>
                item.BookingId == 602 &&
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.CompensationRefund)
            .ToListAsync();

        Assert.Equal(2, refunds.Count);
        var relationRefund = Assert.Single(refunds.Where(item =>
            item.TransactionCode != null &&
            item.TransactionCode.StartsWith(
                "OVERDUE-FUNDED-501-602-",
                StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(700_000m, relationRefund.Amount);
    }

    private static IPaymentService CreatePaymentService(
        ApplicationDbContext db)
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
                    $"overdue-comp-qr-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(
                    InMemoryEventId.TransactionIgnoredWarning))
                .Options;

        return new ApplicationDbContext(options);
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
