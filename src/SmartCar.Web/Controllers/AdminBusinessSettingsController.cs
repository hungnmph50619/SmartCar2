using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBusinessSettingsController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public AdminBusinessSettingsController(
        ApplicationDbContext dbContext,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var depositHoldDays = await GetDepositHoldDaysAsync(cancellationToken);
        return View(depositHoldDays);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        string? depositHoldDays,
        CancellationToken cancellationToken)
    {
        var rawValue = depositHoldDays?.Trim();

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập số ngày giữ cọc.";
            return RedirectToAction(nameof(Index));
        }

        if (rawValue.Any(character => character < '0' || character > '9'))
        {
            TempData["ErrorMessage"] =
                $"Số ngày giữ cọc chỉ được nhập số nguyên từ 0 đến {DepositHoldPolicy.MaxDays}. Không nhập chữ, số âm hoặc số thập phân.";
            return RedirectToAction(nameof(Index));
        }

        if (!int.TryParse(rawValue, out var parsedDays) ||
            parsedDays < 0 ||
            parsedDays > DepositHoldPolicy.MaxDays)
        {
            TempData["ErrorMessage"] =
                $"Số ngày giữ cọc phải là số nguyên từ 0 đến {DepositHoldPolicy.MaxDays}.";
            return RedirectToAction(nameof(Index));
        }

        var oldValue = await GetDepositHoldDaysAsync(cancellationToken);
        if (oldValue == parsedDays)
        {
            TempData["SuccessMessage"] = $"Số ngày giữ cọc hiện tại vẫn là {oldValue} ngày.";
            return RedirectToAction(nameof(Index));
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var updatedAt = DateTime.UtcNow;

        var affectedRows = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [dbo].[BusinessSettings]
            SET [DepositHoldDays] = {parsedDays},
                [UpdatedAt] = {updatedAt},
                [UpdatedByUserId] = {adminId}
            WHERE [BusinessSettingId] = 1
            """,
            cancellationToken);

        if (affectedRows == 0)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO [dbo].[BusinessSettings]
                    ([BusinessSettingId], [DepositHoldDays], [UpdatedAt], [UpdatedByUserId])
                VALUES
                    (1, {parsedDays}, {updatedAt}, {adminId})
                """,
                cancellationToken);
        }

        await _auditService.WriteAsync(
            adminId,
            "UpdateDepositHoldPolicy",
            "BusinessSetting",
            "1",
            $"Thay đổi thời gian giữ cọc sau trả xe: {oldValue} ngày → {parsedDays} ngày.",
            oldValues: JsonSerializer.Serialize(new { DepositHoldDays = oldValue }),
            newValues: JsonSerializer.Serialize(new { DepositHoldDays = parsedDays }),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã đổi thời gian giữ cọc từ {oldValue} ngày thành {parsedDays} ngày. " +
            "Chỉ các đơn thuê tạo mới từ bây giờ mới áp dụng giá trị này.";

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> RefundEligibility(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => new
            {
                item.DepositHoldDaysApplied,
                ReturnedAt = item.VehicleReturn == null
                    ? (DateTime?)null
                    : item.VehicleReturn.ReturnedAt,
                HasPendingDepositRefund = item.Payments.Any(payment =>
                    payment.Type == PaymentType.Refund &&
                    payment.Method == PaymentMethods.DepositRefund &&
                    payment.Status == PaymentStatus.AwaitingRefund),
                OutstandingTrafficFineAmount = item.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.TrafficFine &&
                        (payment.Status == PaymentStatus.Pending ||
                         payment.Status == PaymentStatus.AwaitingConfirmation ||
                         payment.Status == PaymentStatus.Failed))
                    .Sum(payment => (decimal?)payment.Amount) ?? 0m
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (booking is null ||
            !booking.HasPendingDepositRefund ||
            !booking.ReturnedAt.HasValue)
        {
            return Json(new { applies = false });
        }

        var holdDays = DepositHoldPolicy.NormalizeDays(booking.DepositHoldDaysApplied);
        var eligibleAt = DepositHoldPolicy.CalculateEligibleAt(
            booking.ReturnedAt.Value,
            holdDays);
        var vietnamNow = DateTime.UtcNow.AddHours(7);
        var timeEligible = vietnamNow >= eligibleAt;
        var hasOutstandingTrafficFine = booking.OutstandingTrafficFineAmount > 0;
        var isEligible = timeEligible && !hasOutstandingTrafficFine;

        string message;
        if (!timeEligible)
        {
            message =
                $"Booking áp dụng chính sách giữ cọc {holdDays} ngày. " +
                $"Sớm nhất được duyệt hoàn lúc {eligibleAt:dd/MM/yyyy HH:mm}.";
        }
        else if (hasOutstandingTrafficFine)
        {
            message =
                $"Đã đủ thời gian giữ cọc nhưng đơn còn {booking.OutstandingTrafficFineAmount:N0} đồng " +
                "phạt/vi phạm chưa xử lý. Cần đối soát khoản này trước khi duyệt hoàn cọc.";
        }
        else if (holdDays == 0)
        {
            message =
                "Booking áp dụng 0 ngày giữ cọc: đã đủ điều kiện duyệt hoàn ngay sau hậu kiểm.";
        }
        else
        {
            message =
                $"Đã đủ {holdDays} ngày giữ cọc và không còn khoản phạt/vi phạm chưa xử lý. " +
                "Khoản cọc đủ điều kiện để Admin duyệt hoàn.";
        }

        return Json(new
        {
            applies = true,
            holdDays,
            isEligible,
            timeEligible,
            outstandingTrafficFineAmount = booking.OutstandingTrafficFineAmount,
            returnedAt = booking.ReturnedAt.Value.ToString("dd/MM/yyyy HH:mm"),
            eligibleAt = eligibleAt.ToString("dd/MM/yyyy HH:mm"),
            message
        });
    }

    private async Task<int> GetDepositHoldDaysAsync(CancellationToken cancellationToken)
    {
        var values = await _dbContext.Database
            .SqlQueryRaw<int>(
                "SELECT [DepositHoldDays] AS [Value] FROM [dbo].[BusinessSettings] WHERE [BusinessSettingId] = 1")
            .ToListAsync(cancellationToken);

        return values.Count == 0
            ? DepositHoldPolicy.DefaultDays
            : DepositHoldPolicy.NormalizeDays(values[0]);
    }
}
