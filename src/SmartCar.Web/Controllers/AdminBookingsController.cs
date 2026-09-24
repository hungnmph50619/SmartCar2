using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Operations;
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
        // Unreviewed requests belong to Staff, not the Admin approval queue.
        var unreviewedIds = await _dbContext.Bookings.AsNoTracking()
            .Where(item => item.Status == BookingStatus.PendingConfirmation && !item.StaffReviewedAt.HasValue)
            .Select(item => item.BookingId)
            .ToListAsync(cancellationToken);
        bookings = bookings.Where(item => !unreviewedIds.Contains(item.BookingId)).ToList();
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
        if (booking is null) return NotFound();
        if (booking.Status == BookingStatus.PendingConfirmation &&
            !await _dbContext.Bookings.AsNoTracking().AnyAsync(
                item => item.BookingId == id && item.StaffReviewedAt.HasValue,
                cancellationToken)) return NotFound();
        return View(booking);
    }

    [HttpGet]
    public async Task<IActionResult> HoldRestrictions(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-BookingHoldAbusePolicy.RollingWindowHours);
        var events = await _dbContext.Set<BookingHoldEvent>()
            .AsNoTracking()
            .Where(item =>
                !item.IsWaived &&
                item.OccurredAt >= windowStart &&
                item.OccurredAt <= now)
            .OrderByDescending(item => item.OccurredAt)
            .ToListAsync(cancellationToken);

        if (events.Count == 0)
        {
            return View(new AdminHoldRestrictionListViewModel());
        }

        var customerIds = events.Select(item => item.CustomerId).Distinct().ToList();
        var vehicleIds = events.Select(item => item.VehicleId).Distinct().ToList();

        var customers = await _dbContext.Users
            .AsNoTracking()
            .Where(user => customerIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
                user.PhoneNumber
            })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .Where(vehicle => vehicleIds.Contains(vehicle.VehicleId))
            .Select(vehicle => new
            {
                vehicle.VehicleId,
                vehicle.VehicleName,
                vehicle.LicensePlate
            })
            .ToDictionaryAsync(item => item.VehicleId, cancellationToken);

        var items = events
            .GroupBy(item => item.CustomerId)
            .Select(group =>
            {
                customers.TryGetValue(group.Key, out var customer);
                var allTimes = group.Select(item => item.OccurredAt).ToArray();
                var accountRestriction = BookingHoldAbusePolicy.Evaluate(
                    now,
                    allTimes,
                    Array.Empty<DateTime>());

                var cooldowns = group
                    .GroupBy(item => item.VehicleId)
                    .Select(vehicleGroup =>
                    {
                        vehicles.TryGetValue(vehicleGroup.Key, out var vehicle);
                        var vehicleTimes = vehicleGroup.Select(item => item.OccurredAt).ToArray();
                        var restriction = BookingHoldAbusePolicy.Evaluate(
                            now,
                            allTimes,
                            vehicleTimes);

                        return new AdminVehicleCooldownViewModel
                        {
                            VehicleId = vehicleGroup.Key,
                            VehicleName = vehicle?.VehicleName ?? $"Xe #{vehicleGroup.Key}",
                            LicensePlate = vehicle?.LicensePlate ?? string.Empty,
                            TimeoutCount24Hours = vehicleTimes.Length,
                            CooldownUntil = restriction.SameVehicleCooldownUntil
                        };
                    })
                    .OrderByDescending(item => item.CooldownUntil)
                    .ToList();

                return new AdminHoldRestrictionItemViewModel
                {
                    CustomerId = group.Key,
                    CustomerName = customer?.FullName ?? "Khách hàng",
                    CustomerEmail = customer?.Email,
                    CustomerPhone = customer?.PhoneNumber,
                    TimeoutCount24Hours = accountRestriction.TimeoutCount24Hours,
                    LatestTimeoutAt = allTimes.Max(),
                    AccountBlockedUntil = accountRestriction.AccountBlockedUntil,
                    VehicleCooldowns = cooldowns
                };
            })
            .OrderByDescending(item => item.AccountBlockedUntil.HasValue)
            .ThenByDescending(item => item.TimeoutCount24Hours)
            .ThenByDescending(item => item.LatestTimeoutAt)
            .ToList();

        return View(new AdminHoldRestrictionListViewModel { Items = items });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WaiveHoldRestriction(
        string customerId,
        string reason,
        CancellationToken cancellationToken)
    {
        customerId = customerId?.Trim() ?? string.Empty;
        reason = reason?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(customerId) || reason.Length < 10)
        {
            TempData["ErrorMessage"] =
                "Cần chọn đúng khách hàng và nhập lý do gỡ hạn chế tối thiểu 10 ký tự.";
            return RedirectToAction(nameof(HoldRestrictions));
        }

        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-BookingHoldAbusePolicy.RollingWindowHours);
        var activeEvents = await _dbContext.Set<BookingHoldEvent>()
            .Where(item =>
                item.CustomerId == customerId &&
                !item.IsWaived &&
                item.OccurredAt >= windowStart &&
                item.OccurredAt <= now)
            .ToListAsync(cancellationToken);

        if (activeEvents.Count == 0)
        {
            TempData["ErrorMessage"] = "Khách hàng hiện không có lịch sử timeout chưa được gỡ trong 24 giờ gần nhất.";
            return RedirectToAction(nameof(HoldRestrictions));
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        foreach (var holdEvent in activeEvents)
        {
            holdEvent.IsWaived = true;
            holdEvent.WaivedByAdminId = adminId;
            holdEvent.WaivedAt = now;
            holdEvent.WaiveReason = reason;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync(
            adminId,
            "WaiveBookingHoldRestriction",
            nameof(BookingHoldEvent),
            customerId,
            $"Admin gỡ {activeEvents.Count} sự kiện giữ chỗ hết hạn chưa waive trong 24 giờ gần nhất. Lý do: {reason}",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã gỡ hạn chế giữ chỗ hiện tại của khách và ghi audit cho {activeEvents.Count} lần timeout.";
        return RedirectToAction(nameof(HoldRestrictions));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id, CancellationToken cancellationToken)
    {
        var staffReview = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == id)
            .Select(item => new
            {
                item.StaffReviewedAt,
                item.StaffReviewedByStaffId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (staffReview is null)
        {
            return NotFound();
        }

        if (!staffReview.StaffReviewedAt.HasValue ||
            string.IsNullOrWhiteSpace(staffReview.StaffReviewedByStaffId))
        {
            TempData["ErrorMessage"] =
                "Đơn chưa được nhân viên kiểm tra và gửi duyệt. Quản trị viên không thể bỏ qua bước vận hành này.";
            return RedirectToAction(nameof(Details), new { id });
        }

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
                $"Quản trị viên duyệt đơn thuê #{id} sau bước Staff review có trạng thái lưu trên Booking và re-check KYC/xe/lịch hiện tại; chuyển sang Chờ thanh toán.",
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
