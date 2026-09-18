using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffBookingOperationsController : Controller
{
    private readonly IBookingOperationService _operationService;
    private readonly IBookingReviewService _bookingReviewService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;

    public StaffBookingOperationsController(
        IBookingOperationService operationService,
        IBookingReviewService bookingReviewService,
        IAuditService auditService,
        ApplicationDbContext dbContext)
    {
        _operationService = operationService;
        _bookingReviewService = bookingReviewService;
        _auditService = auditService;
        _dbContext = dbContext;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewForApproval(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.StaffReviewedAt.HasValue)
        {
            TempData["SuccessMessage"] =
                "Đơn này đã được nhân viên kiểm tra và đang chờ quản trị viên duyệt.";
            return RedirectToAction("Details", "Staff", new { id = bookingId });
        }

        if (!BookingWorkflowRules.CanStaffReview(booking.Status, booking.StaffReviewedAt))
        {
            TempData["ErrorMessage"] =
                "Đơn không còn ở trạng thái Chờ xác nhận nên không thể gửi duyệt lại.";
            return RedirectToAction("Details", "Staff", new { id = bookingId });
        }

        var result = await _bookingReviewService.ValidateForStaffReviewAsync(
            bookingId,
            cancellationToken);
        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToAction("Details", "Staff", new { id = bookingId });
        }

        // Reload current state after validation so an expiry/state change during review
        // cannot leave a stale "reviewed" flag on an ineligible booking.
        await _dbContext.Entry(booking).ReloadAsync(cancellationToken);
        if (!BookingWorkflowRules.CanStaffReview(booking.Status, booking.StaffReviewedAt))
        {
            TempData["ErrorMessage"] =
                booking.StaffReviewedAt.HasValue
                    ? "Đơn đã được nhân viên khác kiểm tra trước đó."
                    : "Trạng thái đơn vừa thay đổi. Vui lòng tải lại trước khi thao tác.";
            return RedirectToAction("Details", "Staff", new { id = bookingId });
        }

        booking.StaffReviewedAt = DateTime.UtcNow;
        booking.StaffReviewedByStaffId = staffId;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _auditService.WriteAsync(
            staffId,
            "StaffReviewedBooking",
            nameof(Booking),
            bookingId.ToString(),
            $"Nhân viên đã kiểm tra đơn #{bookingId}: tài khoản khách hoạt động, KYC hợp lệ đến ngày trả, xe hoạt động và lịch xe không xung đột theo buffer hiện hành. Đơn được gửi quản trị viên duyệt.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã kiểm tra điều kiện và gửi quản trị viên duyệt. Bước kiểm tra đã được khóa, không cần bấm lại.";
        return RedirectToAction("Details", "Staff", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        CancelBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = ModelState.IsValid
            ? await _operationService.CancelByStaffAsync(
                staffId,
                new CancelBookingRequest(model.BookingId, model.Reason),
                cancellationToken)
            : RefundResult.Failure("Vui lòng nhập lý do hủy đơn.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? result.RefundAmount > 0
                ? $"Đã hủy đơn. Hệ thống đã tạo {result.RefundAmount:N0} đồng chờ quản trị viên duyệt hoàn tiền."
                : "Đã hủy đơn và không phát sinh khoản hoàn mới."
            : string.Join("; ", result.Errors);

        return result.Succeeded
            ? RedirectToAction("Bookings", "Staff")
            : RedirectToAction("Details", "Staff", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNoShow(
        int bookingId,
        bool customerContacted,
        CancellationToken cancellationToken)
    {
        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.MarkNoShowAsync(
            bookingId,
            staffId,
            customerContacted,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận khách không đến. Các khoản hoàn phát sinh đang chờ quản trị viên duyệt trước khi nhân viên chuyển tiền."
            : string.Join("; ", result.Errors);

        return result.Succeeded
            ? RedirectToAction("Bookings", "Staff")
            : RedirectToAction("Details", "Staff", new { id = bookingId });
    }
}
