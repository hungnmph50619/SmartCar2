using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
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
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IDocumentService _documentService;

    public AdminBookingsController(
        IBookingService bookingService,
        IAuditService auditService,
        ApplicationDbContext dbContext,
        IDocumentService documentService)
    {
        _bookingService = bookingService;
        _auditService = auditService;
        _dbContext = dbContext;
        _documentService = documentService;
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
        {
            return View(bookings);
        }

        var keyword = query.Trim();
        var bookingIdText = keyword.TrimStart('#');
        var hasBookingId = int.TryParse(bookingIdText, out var bookingId);

        var filteredBookings = bookings
            .Where(booking =>
                (hasBookingId && booking.BookingId == bookingId) ||
                booking.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(booking.CustomerPhone) &&
                 booking.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                booking.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                booking.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return View(filteredBookings);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(id, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var customerCreatedAt = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == booking.CustomerId)
            .Select(user => (DateTime?)user.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var customerBookingStatuses = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.CustomerId == booking.CustomerId)
            .Select(item => item.Status)
            .ToListAsync(cancellationToken);

        var documents = await _documentService.GetCustomerDocumentsAsync(
            booking.CustomerId,
            cancellationToken);
        var citizenFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
        var citizenBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenIdBack);
        var drivingLicense = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);
        var drivingLicenseBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicenseBack);

        var citizenVerified = citizenFront is not null &&
                              citizenBack is not null &&
                              citizenFront.Status == DocumentStatus.Verified &&
                              citizenBack.Status == DocumentStatus.Verified &&
                              citizenFront.HasRequiredData &&
                              citizenBack.HasRequiredData &&
                              citizenFront.ExpiryDate.HasValue &&
                              citizenFront.ExpiryDate.Value.Date >= booking.ReturnDate.Date;

        var drivingLicenseVerified = drivingLicense is not null &&
                                     drivingLicenseBack is not null &&
                                     drivingLicense.Status == DocumentStatus.Verified &&
                                     drivingLicenseBack.Status == DocumentStatus.Verified &&
                                     drivingLicense.HasRequiredData &&
                                     drivingLicenseBack.HasRequiredData &&
                                     drivingLicense.ExpiryDate.HasValue &&
                                     drivingLicense.ExpiryDate.Value.Date >= booking.ReturnDate.Date;

        ViewBag.CustomerCreatedAt = customerCreatedAt;
        ViewBag.CustomerTotalBookingCount = customerBookingStatuses.Count;
        ViewBag.CustomerCompletedBookingCount = customerBookingStatuses.Count(status => status == BookingStatus.Completed);
        ViewBag.CustomerCancelledBookingCount = customerBookingStatuses.Count(status =>
            status is BookingStatus.Cancelled or BookingStatus.Rejected);
        ViewBag.CustomerNoShowCount = customerBookingStatuses.Count(status => status == BookingStatus.NoShow);
        ViewBag.CitizenIdentityVerified = citizenVerified;
        ViewBag.DrivingLicenseVerified = drivingLicenseVerified;
        ViewBag.CustomerKycVerified = citizenVerified && drivingLicenseVerified;

        return View(booking);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id, CancellationToken cancellationToken)
    {
        var result = await _bookingService.ConfirmAsync(id, cancellationToken);
        SetMessage(result, "Đã xác nhận đơn thuê và tạo khoản thanh toán.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "Confirm",
                id,
                $"Xác nhận đơn thuê #{id} và chuyển sang chờ thanh toán.",
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
                "Reject",
                model.BookingId,
                $"Từ chối đơn thuê #{model.BookingId}. Lý do: {model.Reason.Trim()}",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(int id, CancellationToken cancellationToken)
    {
        var result = await _bookingService.MarkReadyForPickupAsync(id, cancellationToken);
        SetMessage(result, "Đã đánh dấu xe sẵn sàng bàn giao.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "MarkReady",
                id,
                $"Đánh dấu xe của đơn #{id} đã sẵn sàng bàn giao.",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private void SetMessage(OperationResult result, string successMessage)
    {
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? successMessage
            : string.Join("; ", result.Errors);
    }

    private Task WriteAuditAsync(
        string action,
        int bookingId,
        string description,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        return _auditService.WriteAsync(
            adminId,
            action,
            nameof(Booking),
            bookingId.ToString(),
            description,
            ipAddress: ipAddress,
            cancellationToken: cancellationToken);
    }
}
