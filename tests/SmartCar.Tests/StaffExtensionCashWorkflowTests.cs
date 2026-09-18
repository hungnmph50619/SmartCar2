using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
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
    public async Task CollectExtensionCash_AppliesApprovedExtensionAtomically()
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

        var payment = Assert.Single(booking.Payments.Where(item =>
            item.Type == PaymentType.Extension));
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
            CancellationToken cancellationToken = default) =>
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
