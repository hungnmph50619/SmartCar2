using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using Xunit;

namespace SmartCar.Tests;

public sealed class StaffQrRejectionWorkflowTests
{
    [Fact]
    public async Task RejectQr_ForwardsStaffReasonToPaymentService()
    {
        await using var db = CreateDbContext();
        var paymentService = new CapturingPaymentService();
        var controller = CreateController(db, paymentService);

        var result = await controller.RejectQr(
            paymentId: 42,
            reason: "  Không thấy giao dịch trên sao kê ngân hàng.  ",
            cancellationToken: default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Reconciliation", redirect.ActionName);
        Assert.Equal(42, paymentService.RejectedPaymentId);
        Assert.Equal("staff-1", paymentService.ActorId);
        Assert.Equal(
            "Không thấy giao dịch trên sao kê ngân hàng.",
            paymentService.RejectionReason);
    }

    private static StaffPaymentsController CreateController(
        ApplicationDbContext db,
        CapturingPaymentService paymentService)
    {
        var controller = new StaffPaymentsController(
            db,
            paymentService,
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
                .UseInMemoryDatabase($"staff-qr-reject-{Guid.NewGuid():N}")
                .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class CapturingPaymentService : IPaymentService
    {
        public int? RejectedPaymentId { get; private set; }
        public string? ActorId { get; private set; }
        public string? RejectionReason { get; private set; }

        public Task<IReadOnlyList<AdminPaymentListItemDto>> GetAdminPaymentsAsync(
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
            string rejectionReason,
            CancellationToken cancellationToken = default)
        {
            RejectedPaymentId = paymentId;
            ActorId = actorId;
            RejectionReason = rejectionReason;
            return Task.FromResult(OperationResult.Success());
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
