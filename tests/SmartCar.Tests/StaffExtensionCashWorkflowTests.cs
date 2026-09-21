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
using Xunit;

namespace SmartCar.Tests;

public sealed class StaffExtensionCashWorkflowTests
{
    [Fact]
    public async Task CollectExtensionCash_AppliesApprovedExtensionAndPayment()
    {
        await using var db = CreateDbContext();
        var originalReturn = new DateTime(2026, 9, 20, 10, 0, 0);
        var requestedReturn = originalReturn.AddDays(1);

        db.Bookings.Add(new Booking
        {
            BookingId = 101,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = originalReturn.AddDays(-2),
            ReturnDate = originalReturn,
            DailyPrice = 500_000m,
            NumberOfDays = 2,
            RentalAmount = 1_000_000m,
            DepositAmount = 3_000_000m,
            TotalAmount = 1_000_000m,
            Status = BookingStatus.Rented
        });
        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 501,
            BookingId = 101,
            OriginalReturnDate = originalReturn,
            RequestedReturnDate = requestedReturn,
            AdditionalDays = 1,
            AdditionalAmount = 500_000m,
            Status = BookingExtensionStatus.Approved,
            RequestedAt = DateTime.UtcNow.AddMinutes(-10),
            DecidedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        db.Payments.Add(new Payment
        {
            BookingId = 101,
            Type = PaymentType.Extension,
            Amount = 500_000m,
            Method = PaymentMethods.NotSelected,
            Status = PaymentStatus.Pending
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.CollectExtensionCash(101, default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);

        var booking = await db.Bookings
            .Include(item => item.Extensions)
            .Include(item => item.Payments)
            .SingleAsync(item => item.BookingId == 101);

        var extension = Assert.Single(booking.Extensions);
        Assert.Equal(BookingExtensionStatus.Paid, extension.Status);
        Assert.NotNull(extension.PaidAt);
        Assert.Equal(requestedReturn, booking.ReturnDate);
        Assert.Equal(1_500_000m, booking.RentalAmount);
        Assert.Equal(1_500_000m, booking.TotalAmount);

        var payment = Assert.Single(
            booking.Payments,
            item => item.Type == PaymentType.Extension);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(PaymentMethods.Cash, payment.Method);
        Assert.Equal(500_000m, payment.Amount);
        Assert.NotNull(payment.PaidAt);
    }

    [Fact]
    public async Task CollectExtensionCash_RejectsWhenQrIsAwaitingReconciliation()
    {
        await using var db = CreateDbContext();
        var originalReturn = new DateTime(2026, 9, 20, 10, 0, 0);

        db.Bookings.Add(new Booking
        {
            BookingId = 102,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = originalReturn.AddDays(-2),
            ReturnDate = originalReturn,
            DailyPrice = 500_000m,
            NumberOfDays = 2,
            RentalAmount = 1_000_000m,
            DepositAmount = 3_000_000m,
            TotalAmount = 1_000_000m,
            Status = BookingStatus.Rented
        });
        db.BookingExtensions.Add(new BookingExtension
        {
            BookingExtensionId = 502,
            BookingId = 102,
            OriginalReturnDate = originalReturn,
            RequestedReturnDate = originalReturn.AddDays(1),
            AdditionalDays = 1,
            AdditionalAmount = 500_000m,
            Status = BookingExtensionStatus.Approved
        });
        db.Payments.Add(new Payment
        {
            BookingId = 102,
            Type = PaymentType.Extension,
            Amount = 500_000m,
            Method = PaymentMethods.BankQr,
            Status = PaymentStatus.AwaitingConfirmation
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        await controller.CollectExtensionCash(102, default);

        var extension = await db.BookingExtensions.SingleAsync();
        var payment = await db.Payments.SingleAsync();
        var booking = await db.Bookings.SingleAsync();

        Assert.Equal(BookingExtensionStatus.Approved, extension.Status);
        Assert.Equal(PaymentStatus.AwaitingConfirmation, payment.Status);
        Assert.Equal(originalReturn, booking.ReturnDate);
    }

    [Fact]
    public async Task CollectOverdueCompensationDebtCash_PaysPendingDebtAndPreservesSourceCode()
    {
        await using var db = CreateDbContext();

        db.Bookings.Add(new Booking
        {
            BookingId = 103,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = DateTime.Now.AddDays(-2),
            ReturnDate = DateTime.Now.AddHours(-1),
            DailyPrice = 500_000m,
            NumberOfDays = 2,
            RentalAmount = 1_000_000m,
            DepositAmount = 300_000m,
            TotalAmount = 1_000_000m,
            Status = BookingStatus.PendingInspection
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 204,
            CustomerId = "customer-2",
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
        db.Payments.Add(new Payment
        {
            BookingId = 103,
            Type = PaymentType.OverdueCompensationDebt,
            Amount = 700_000m,
            Method = PaymentMethods.NotSelected,
            Status = PaymentStatus.Pending,
            TransactionCode = "OVERDUE-DEBT-103-204-20260920120000"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        await controller.CollectOverdueCompensationDebtCash(103, default);

        var payment = await db.Payments.SingleAsync(item =>
            item.BookingId == 103 &&
            item.Type == PaymentType.OverdueCompensationDebt);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(PaymentMethods.Cash, payment.Method);
        Assert.NotNull(payment.PaidAt);
        Assert.Equal(
            "OVERDUE-DEBT-103-204-20260920120000",
            payment.TransactionCode);

        var compensationRefund = await db.Payments.SingleAsync(item =>
            item.BookingId == 204 &&
            item.Type == PaymentType.Refund &&
            item.Method == PaymentMethods.CompensationRefund);
        Assert.Equal(700_000m, compensationRefund.Amount);
        Assert.Equal(PaymentStatus.AwaitingRefund, compensationRefund.Status);
    }


    [Fact]
    public async Task CollectOverdueCompensationDebtCash_DoesNotCountFundedCompensationFromDifferentSourceRelation()
    {
        await using var db = CreateDbContext();

        db.Bookings.AddRange(
            new Booking
            {
                BookingId = 107,
                CustomerId = "customer-1",
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
                BookingId = 208,
                CustomerId = "customer-2",
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

        db.Payments.AddRange(
            new Payment
            {
                BookingId = 107,
                Type = PaymentType.OverdueCompensationDebt,
                Amount = 700_000m,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending,
                TransactionCode = "OVERDUE-DEBT-107-208-20260920140000"
            },
            new Payment
            {
                BookingId = 208,
                Type = PaymentType.Refund,
                Amount = 200_000m,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.AwaitingRefund,
                TransactionCode = "OVERDUE-FUNDED-999-208-20260920130000000"
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        await controller.CollectOverdueCompensationDebtCash(107, default);

        var refunds = await db.Payments
            .Where(item =>
                item.BookingId == 208 &&
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.CompensationRefund)
            .ToListAsync();

        Assert.Equal(2, refunds.Count);
        var relationRefund = Assert.Single(refunds.Where(item =>
            item.TransactionCode != null &&
            item.TransactionCode.StartsWith(
                "OVERDUE-FUNDED-107-208-",
                StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(700_000m, relationRefund.Amount);
    }

    [Fact]
    public async Task CollectOverdueCompensationDebtCash_DoesNotDuplicateLegacyFullCompensationRefund()
    {
        await using var db = CreateDbContext();

        db.Bookings.AddRange(
            new Booking
            {
                BookingId = 105,
                CustomerId = "customer-1",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddDays(-2),
                ReturnDate = DateTime.Now.AddHours(-1),
                DailyPrice = 500_000m,
                NumberOfDays = 2,
                RentalAmount = 1_000_000m,
                DepositAmount = 700_000m,
                TotalAmount = 1_000_000m,
                Status = BookingStatus.PendingInspection
            },
            new Booking
            {
                BookingId = 206,
                CustomerId = "customer-2",
                VehicleId = 10,
                PickupDate = DateTime.Now.AddHours(-1),
                ReturnDate = DateTime.Now.AddDays(1),
                DailyPrice = 1_000_000m,
                NumberOfDays = 1,
                RentalAmount = 1_000_000m,
                DepositAmount = 0m,
                TotalAmount = 1_000_000m,
                Status = BookingStatus.Cancelled
            });

        db.Payments.AddRange(
            new Payment
            {
                BookingId = 105,
                Type = PaymentType.AdditionalCharge,
                Amount = 700_000m,
                Method = PaymentMethods.DepositDeduction,
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow.AddMinutes(-5),
                TransactionCode = "OVERDUE-COMP-105-206-20260920115500"
            },
            new Payment
            {
                BookingId = 105,
                Type = PaymentType.OverdueCompensationDebt,
                Amount = 300_000m,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending,
                TransactionCode = "OVERDUE-DEBT-105-206-20260920120000"
            },
            new Payment
            {
                BookingId = 206,
                Type = PaymentType.Refund,
                Amount = 1_000_000m,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        await controller.CollectOverdueCompensationDebtCash(105, default);

        var debt = await db.Payments.SingleAsync(item =>
            item.BookingId == 105 &&
            item.Type == PaymentType.OverdueCompensationDebt);
        Assert.Equal(PaymentStatus.Paid, debt.Status);

        var compensationRefunds = await db.Payments
            .Where(item =>
                item.BookingId == 206 &&
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.CompensationRefund)
            .ToListAsync();

        var compensationRefund = Assert.Single(compensationRefunds);
        Assert.Equal(1_000_000m, compensationRefund.Amount);
    }

    [Fact]
    public async Task CollectOverdueCompensationDebtCash_RejectsWhenQrIsAwaitingReconciliation()
    {
        await using var db = CreateDbContext();

        db.Bookings.Add(new Booking
        {
            BookingId = 104,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = DateTime.Now.AddDays(-2),
            ReturnDate = DateTime.Now.AddHours(-1),
            DailyPrice = 500_000m,
            NumberOfDays = 2,
            RentalAmount = 1_000_000m,
            DepositAmount = 300_000m,
            TotalAmount = 1_000_000m,
            Status = BookingStatus.PendingInspection
        });
        db.Payments.Add(new Payment
        {
            BookingId = 104,
            Type = PaymentType.OverdueCompensationDebt,
            Amount = 700_000m,
            Method = PaymentMethods.BankQr,
            Status = PaymentStatus.AwaitingConfirmation,
            TransactionCode = "OVERDUE-DEBT-104-205-20260920120000"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        await controller.CollectOverdueCompensationDebtCash(104, default);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.AwaitingConfirmation, payment.Status);
        Assert.Equal(PaymentMethods.BankQr, payment.Method);
        Assert.Null(payment.PaidAt);
    }

    private static StaffPaymentsController CreateController(
        ApplicationDbContext db)
    {
        var controller = new StaffPaymentsController(
            db,
            new PaymentServiceStub(),
            new AuditServiceStub());

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

    private static ApplicationDbContext CreateDbContext()
    {
        var options =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(
                    $"staff-extension-cash-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(
                    InMemoryEventId.TransactionIgnoredWarning))
                .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class PaymentServiceStub : IPaymentService
    {
        public Task<IReadOnlyList<AdminPaymentListItemDto>>
            GetAdminPaymentsAsync(
                PaymentStatus? status = null,
                PaymentType? type = null,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdminPaymentListItemDto>>(
                Array.Empty<AdminPaymentListItemDto>());

        public Task<OperationResult> SubmitQrPaymentAsync(
            int bookingId,
            string customerId,
            PaymentType paymentType,
            string actorId,
            CancellationToken cancellationToken = default,
            int? paymentId = null) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> ConfirmQrPaymentAsync(
            int paymentId,
            string actorId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> RejectQrPaymentAsync(
            int paymentId,
            string actorId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
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
