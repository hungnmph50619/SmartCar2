using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffPaymentsController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPaymentService _paymentService;
    private readonly IAuditService _auditService;

    public StaffPaymentsController(
        ApplicationDbContext dbContext,
        IPaymentService paymentService,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _paymentService = paymentService;
        _auditService = auditService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCounterQr(
        int bookingId,
        bool transferConfirmed,
        CancellationToken cancellationToken)
    {
        if (!transferConfirmed)
        {
            TempData["ErrorMessage"] =
                "Chỉ ghi nhận chuyển khoản sau khi khách đã quét QR/thực hiện chuyển tiền tại quầy.";
            return RedirectToStaffDetails(bookingId);
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new
            {
                item.BookingId,
                item.CustomerId,
                item.Status
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê.";
            return RedirectToAction("Bookings", "Staff");
        }

        if (booking.Status != BookingStatus.PendingPayment)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đã được Admin duyệt và đang Chờ thanh toán mới được báo chuyển khoản tại quầy.";
            return RedirectToStaffDetails(bookingId);
        }

        var result = await _paymentService.SubmitQrPaymentAsync(
            bookingId,
            booking.CustomerId,
            PaymentType.Rental,
            cancellationToken);

        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToStaffDetails(bookingId);
        }

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var bookingRow = await _dbContext.Bookings
            .FirstAsync(item => item.BookingId == bookingId, cancellationToken);
        bookingRow.ReservationExpiresAt = DateTime.UtcNow
            .AddMinutes(RentalPolicy.BookingTransferReconciliationHoldMinutes);

        // PaymentService dùng chung cho Customer và Staff. Sau khi Staff thực hiện tại quầy,
        // sửa audit thanh toán vừa sinh về đúng actor thay vì để lịch sử ghi nhầm là Customer tự báo.
        var rentalPaymentId = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.BookingId == bookingId &&
                payment.Type == PaymentType.Rental &&
                payment.Method == PaymentMethods.BankQr &&
                payment.Status == PaymentStatus.AwaitingConfirmation)
            .OrderByDescending(payment => payment.PaymentId)
            .Select(payment => payment.PaymentId)
            .FirstOrDefaultAsync(cancellationToken);

        var correctedExistingAudit = false;
        if (rentalPaymentId > 0)
        {
            var paymentAudit = await _dbContext.AuditLogs
                .Where(log =>
                    log.Action == "SubmitQrPayment" &&
                    log.EntityName == nameof(Payment) &&
                    log.EntityId == rentalPaymentId.ToString() &&
                    log.UserId == booking.CustomerId)
                .OrderByDescending(log => log.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (paymentAudit is not null)
            {
                paymentAudit.UserId = staffId;
                paymentAudit.Action = "StaffSubmitCounterQr";
                paymentAudit.Description =
                    $"Nhân viên tại quầy xác nhận khách đã thực hiện chuyển khoản/QR cho đơn #{bookingId}; " +
                    $"giao dịch chờ Admin đối soát tối đa {RentalPolicy.BookingTransferReconciliationHoldMinutes} phút.";
                paymentAudit.IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                correctedExistingAudit = true;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!correctedExistingAudit)
        {
            await _auditService.WriteAsync(
                staffId,
                "StaffSubmitCounterQr",
                nameof(Payment),
                rentalPaymentId > 0 ? rentalPaymentId.ToString() : bookingId.ToString(),
                $"Nhân viên tại quầy xác nhận khách đã thực hiện chuyển khoản/QR cho đơn #{bookingId}; " +
                $"giao dịch chờ Admin đối soát tối đa {RentalPolicy.BookingTransferReconciliationHoldMinutes} phút.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);
        }

        TempData["SuccessMessage"] =
            $"Đã ghi nhận khách báo chuyển khoản. Admin có tối đa {RentalPolicy.BookingTransferReconciliationHoldMinutes} phút để đối soát; giao dịch chưa được coi là đã thanh toán.";
        return RedirectToStaffDetails(bookingId);
    }

    private IActionResult RedirectToStaffDetails(int bookingId) =>
        RedirectToAction("Details", "Staff", new { id = bookingId });
}
