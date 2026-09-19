using System.Security.Claims;
using SmartCar.Infrastructure.Services;
using System.Data;
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
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(RentalPolicySnapshot model, CancellationToken cancellationToken)
    {
        foreach (var field in new[] { "Version", "DepositHoldDays", "DepositPercent", "IncludedKilometersPerDay",
            "ExcessKilometerFee", "LateReturnFeeMultiplier", "LateReturnGraceMinutes", "IncludedDeliveryDistanceKm",
            "BaseDeliveryFee", "DeliveryFeePerExtraKm", "MaxDeliveryDistanceKm", "TrafficFineTerms", "DamageCompensationTerms" })
        {
            if (!Request.Form.ContainsKey(field) || string.IsNullOrWhiteSpace(Request.Form[field]))
                ModelState.AddModelError(field, "Vui lòng nhập đầy đủ thông tin chính sách.");
        }
        if (!ModelState.IsValid) return View("Index", model);
        model.TrafficFineTerms = model.TrafficFineTerms.Trim();
        model.DamageCompensationTerms = model.DamageCompensationTerms.Trim();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // Serialize concurrent configuration writers before reading the version (avoids lock conversion deadlocks).
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE [dbo].[BusinessSettings] WITH (UPDLOCK, HOLDLOCK) SET [DepositHoldDays] = [DepositHoldDays] WHERE [BusinessSettingId] = 1", cancellationToken);
        var oldPolicy = await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken);
        if (model.Version != oldPolicy.Version)
        {
            await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(string.Empty, "Cấu hình đã được người khác thay đổi. Hãy tải lại trang và kiểm tra trước khi lưu.");
            return View("Index", model);
        }
        model.Version = Guid.NewGuid().ToString("N");
        var json = model.ToJson();
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var updatedAt = DateTime.UtcNow;
        var affected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [dbo].[BusinessSettings] SET [DepositHoldDays] = {model.DepositHoldDays}, [PolicyJson] = {json}, [UpdatedAt] = {updatedAt}, [UpdatedByUserId] = {adminId} WHERE [BusinessSettingId] = 1", cancellationToken);
        if (affected == 0)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [dbo].[BusinessSettings] ([BusinessSettingId], [DepositHoldDays], [PolicyJson], [UpdatedAt], [UpdatedByUserId]) VALUES (1, {model.DepositHoldDays}, {json}, {updatedAt}, {adminId})", cancellationToken);
        await _auditService.WriteAsync(adminId, "UpdateRentalPolicy", "BusinessSetting", "1",
            "Cập nhật chính sách cọc, quãng đường, trả muộn, giao xe và điều khoản. Chỉ áp dụng cho đơn mới.",
            oldValues: oldPolicy.ToJson(), newValues: json,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã lưu chính sách. Đơn mới áp dụng cấu hình mới; đơn đã tạo giữ nguyên chính sách và điều khoản.";
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

