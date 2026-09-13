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
        await _auditService.WriteAsync(
            staffId,
            "StaffSubmitCounterQr",
            nameof(Payment),
            bookingId.ToString(),
            $"Nhân viên tại quầy xác nhận khách đã thực hiện chuyển khoản/QR cho đơn #{bookingId}; giao dịch chuyển sang chờ Admin đối soát.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã ghi nhận khách báo chuyển khoản. Giao dịch đang chờ Admin đối soát; chưa được coi là đã thanh toán.";
        return RedirectToStaffDetails(bookingId);
    }

    private IActionResult RedirectToStaffDetails(int bookingId) =>
        RedirectToAction("Details", "Staff", new { id = bookingId });
}
