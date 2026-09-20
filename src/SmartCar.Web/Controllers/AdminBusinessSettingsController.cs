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
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBusinessSettingsController : Controller
{
    private static readonly HashSet<string> CustomerNoticeGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "deposit",
        "mileage",
        "late",
        "delivery",
        "reservation",
        "cancellation",
        "noshow",
        "terms"
    };

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
        var currentPolicy = await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken);
        ViewData["CurrentPolicy"] = currentPolicy;
        return View(currentPolicy);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string group, string version, CancellationToken cancellationToken)
    {
        var fields = SmartCar.Web.ViewModels.BusinessPolicyGroups.Fields(group);
        if (fields.Length == 0) return BadRequest("Nhóm chính sách không hợp lệ.");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE [dbo].[BusinessSettings] WITH (UPDLOCK, HOLDLOCK) SET [DepositHoldDays] = [DepositHoldDays] WHERE [BusinessSettingId] = 1", cancellationToken);
        var oldPolicy = await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken);
        var model = RentalPolicySnapshot.FromJson(oldPolicy.ToJson());
        // Only bind the selected group. Other settings always come from the database.
        await TryUpdateModelAsync(model, "", metadata => fields.Contains(metadata.PropertyName));
        foreach (var field in fields)
        {
            if (!Request.Form.ContainsKey(field) || string.IsNullOrWhiteSpace(Request.Form[field]))
                ModelState.AddModelError(field, "Vui lòng nhập đầy đủ thông tin.");
        }
        var conflict = string.IsNullOrWhiteSpace(version) || version != oldPolicy.Version;
        if (conflict)
            ModelState.AddModelError(string.Empty, "Chính sách đã thay đổi từ khi bạn mở trang. Hãy tải lại để đối chiếu trước khi sửa tiếp.");
        if (!ModelState.IsValid)
        {
            await transaction.RollbackAsync(cancellationToken);
            ViewData["CurrentPolicy"] = oldPolicy;
            ViewData["EditingGroup"] = group;
            ViewData["PolicyConflict"] = conflict;
            // Keep the submitted version: a stale form must not become valid by submitting twice.
            model.Version = version;
            return View("Index", model);
        }
        model.TrafficFineTerms = model.TrafficFineTerms.Trim();
        model.DamageCompensationTerms = model.DamageCompensationTerms.Trim();
        model.Version = Guid.NewGuid().ToString("N");
        var json = model.ToJson();
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var updatedAt = DateTime.UtcNow;
        var affected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [dbo].[BusinessSettings] SET [DepositHoldDays] = {model.DepositHoldDays}, [PolicyJson] = {json}, [UpdatedAt] = {updatedAt}, [UpdatedByUserId] = {adminId} WHERE [BusinessSettingId] = 1", cancellationToken);
        if (affected == 0)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [dbo].[BusinessSettings] ([BusinessSettingId], [DepositHoldDays], [PolicyJson], [UpdatedAt], [UpdatedByUserId]) VALUES (1, {model.DepositHoldDays}, {json}, {updatedAt}, {adminId})", cancellationToken);
        var auditDescription = BusinessPolicyAuditFormatter.BuildSummary(oldPolicy.ToJson(), json);
        await _auditService.WriteAsync(adminId, "UpdateRentalPolicy", "BusinessSetting", "1",
            $"{auditDescription}. Áp dụng cho đơn mới.",
            oldValues: oldPolicy.ToJson(), newValues: json,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken: cancellationToken);

        if (CustomerNoticeGroups.Contains(group) &&
            BusinessPolicyAuditFormatter.GetChanges(oldPolicy.ToJson(), json).Count > 0)
        {
            await AddCustomerPolicyNotificationsAsync(
                group,
                auditDescription,
                updatedAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        TempData["SuccessMessage"] = $"Đã lưu {SmartCar.Web.ViewModels.BusinessPolicyGroups.Title(group)}. Áp dụng cho đơn mới; đơn cũ giữ nguyên.";
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

    private async Task AddCustomerPolicyNotificationsAsync(
        string group,
        string auditDescription,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var customerRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Customer)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(customerRoleId))
        {
            return;
        }

        var customerIds = await _dbContext.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.RoleId == customerRoleId)
            .Join(
                _dbContext.Users.AsNoTracking().Where(user => user.IsActive),
                userRole => userRole.UserId,
                user => user.Id,
                (userRole, user) => user.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (customerIds.Count == 0)
        {
            return;
        }

        var title = $"Cập nhật chính sách {SmartCar.Web.ViewModels.BusinessPolicyGroups.Title(group)}";
        var vietnamUpdatedAt = updatedAtUtc.AddHours(7);
        var summary = auditDescription.Length > 650
            ? auditDescription[..647] + "..."
            : auditDescription;
        var message =
            $"{summary}. Áp dụng cho đơn tạo từ {vietnamUpdatedAt:dd/MM/yyyy HH:mm}. " +
            "Đơn đã đặt trước thời điểm này giữ nguyên chính sách đã chốt.";

        _dbContext.Notifications.AddRange(
            customerIds.Select(customerId => new Notification
            {
                UserId = customerId,
                Title = title,
                Message = message
            }));

        await _dbContext.SaveChangesAsync(cancellationToken);
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

