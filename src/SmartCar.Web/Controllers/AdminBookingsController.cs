using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBookingsController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IBookingReviewService _bookingReviewService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;

    public AdminBookingsController(
        IBookingService bookingService,
        IBookingReviewService bookingReviewService,
        IAuditService auditService,
        ApplicationDbContext dbContext)
    {
        _bookingService = bookingService;
        _bookingReviewService = bookingReviewService;
        _auditService = auditService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        BookingStatus? status,
        string? query,
        CancellationToken cancellationToken)
    {
        ViewBag.Status = status;
        ViewBag.Query = query;

        var bookings = await _bookingService.GetAdminBookingsAsync(status, cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
            return View(bookings);

        var keyword = query.Trim();
        var bookingIdText = keyword.TrimStart('#');
        var hasBookingId = int.TryParse(bookingIdText, out var bookingId);

        return View(bookings
            .Where(booking =>
                (hasBookingId && booking.BookingId == bookingId)
                || booking.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(booking.CustomerPhone)
                    && booking.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                || booking.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || booking.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(id, cancellationToken);
        return booking is null ? NotFound() : View(booking);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id, CancellationToken cancellationToken)
    {
        var staffReviewed = await _dbContext.AuditLogs
            .AsNoTracking()
            .AnyAsync(log =>
                log.Action == "StaffReviewedBooking" &&
                log.EntityName == "Booking" &&
                log.EntityId == id.ToString(),
                cancellationToken);

        if (!staffReviewed)
        {
            TempData["ErrorMessage"] =
                "Đơn chưa có bước kiểm tra của nhân viên. Nhân viên phải kiểm tra KYC, xe và lịch thuê trước khi quản trị viên duyệt.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // AuditLog chỉ chứng minh nhân viên đã từng kiểm tra. Trước khi Admin duyệt phải
        // kiểm tra lại dữ liệu hiện tại để không duyệt dựa trên KYC/lịch xe đã lỗi thời.
        var currentReview = await _bookingReviewService.ValidateForStaffReviewAsync(id, cancellationToken);
        if (!currentReview.Succeeded)
        {
            TempData["ErrorMessage"] =
                "Đơn không còn đủ điều kiện duyệt theo dữ liệu hiện tại: " +
                string.Join("; ", currentReview.Errors);
            return RedirectToAction(nameof(Details), new { id });
        }

        var result = await _bookingService.ConfirmAsync(id, cancellationToken);
        SetMessage(result, "Đã duyệt đơn thuê và tạo khoản thanh toán cho khách.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "AdminApproveBooking",
                id,
                $"Quản trị viên duyệt đơn thuê #{id} sau bước kiểm tra của nhân viên và re-check KYC/xe/lịch hiện tại; chuyển sang Chờ thanh toán.",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        RejectBookingViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do từ chối.";
            return RedirectToAction(nameof(Details), new { id = model.BookingId });
        }

        var result = await _bookingService.RejectAsync(
            model.BookingId,
            model.Reason,
            cancellationToken);

        SetMessage(result, "Đã từ chối đơn thuê.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "AdminRejectBooking",
                model.BookingId,
                $"Quản trị viên từ chối đơn thuê #{model.BookingId}. Lý do: {model.Reason.Trim()}",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id = model.BookingId });
    }

    private void SetMessage(OperationResult result, string successMessage)
    {
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? successMessage
            : string.Join("; ", result.Errors);
    }

    private async Task WriteAuditAsync(
        string action,
        int bookingId,
        string description,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        await _auditService.WriteAsync(
            adminId,
            action,
            nameof(Booking),
            bookingId.ToString(),
            description,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }
}