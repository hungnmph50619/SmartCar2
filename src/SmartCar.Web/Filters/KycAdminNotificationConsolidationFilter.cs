using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Filters;

/// <summary>
/// DocumentService keeps its existing per-document notifications for backwards compatibility,
/// but the customer KYC workflow should alert admins only after the whole KYC package is ready.
/// This filter removes the notification produced by the current partial submit and creates one
/// actionable notification once CCCD + GPLX (both sides) are all pending.
/// </summary>
public sealed class KycAdminNotificationConsolidationFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> CustomerKycSubmitActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SubmitCitizenId",
        "SubmitDrivingLicense",
        "VerifyAndSubmitCitizenId"
    };

    private static readonly string[] LegacyKycNotificationTitles =
    {
        "Có CCCD chờ xác minh",
        "Có GPLX chờ xác minh",
        "Có giấy tờ chờ xác minh"
    };

    private readonly ApplicationDbContext _dbContext;

    public KycAdminNotificationConsolidationFilter(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var action = context.ActionDescriptor.RouteValues.TryGetValue("action", out var actionName)
            ? actionName
            : null;

        if (string.IsNullOrWhiteSpace(action) ||
            !CustomerKycSubmitActions.Contains(action) ||
            !context.HttpContext.User.IsInRole(RoleNames.Customer))
        {
            await next();
            return;
        }

        var startedAt = DateTime.UtcNow.AddSeconds(-2);
        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        var customerId = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return;
        }

        var cancellationToken = context.HttpContext.RequestAborted;
        var adminRoleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .Where(item => item.RoleId == adminRoleId)
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (adminIds.Count == 0)
        {
            return;
        }

        // Remove only notifications created by the current action. Older KYC work for other
        // customers must never be touched.
        var partialNotifications = await _dbContext.Notifications
            .Where(item => adminIds.Contains(item.UserId) &&
                           item.CreatedAt >= startedAt &&
                           LegacyKycNotificationTitles.Contains(item.Title))
            .ToListAsync(cancellationToken);
        if (partialNotifications.Count > 0)
        {
            _dbContext.Notifications.RemoveRange(partialNotifications);
        }

        var requiredTypes = new[]
        {
            DocumentTypes.CitizenId,
            DocumentTypes.CitizenIdBack,
            DocumentTypes.DrivingLicense,
            DocumentTypes.DrivingLicenseBack
        };

        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document => document.CustomerId == customerId && requiredTypes.Contains(document.DocumentType))
            .Select(document => new { document.DocumentType, document.Status })
            .ToListAsync(cancellationToken);

        var completePendingPackage = requiredTypes.All(type =>
            documents.Any(document => document.DocumentType == type && document.Status == DocumentStatus.Pending));

        if (completePendingPackage)
        {
            var customerName = await _dbContext.Users
                .Where(user => user.Id == customerId)
                .Select(user => user.FullName)
                .FirstOrDefaultAsync(cancellationToken) ?? "Khách hàng";

            var notificationTitle = $"Hồ sơ KYC chờ duyệt|{customerId}";
            var existingAdminIds = await _dbContext.Notifications
                .AsNoTracking()
                .Where(item => adminIds.Contains(item.UserId) &&
                               !item.IsRead &&
                               item.Title == notificationTitle)
                .Select(item => item.UserId)
                .ToListAsync(cancellationToken);

            foreach (var adminId in adminIds.Except(existingAdminIds))
            {
                _dbContext.Notifications.Add(new Notification
                {
                    UserId = adminId,
                    Title = notificationTitle,
                    Message = $"{customerName} đã gửi đủ CCCD và GPLX. Hãy mở hồ sơ để đối chiếu cả hai giấy tờ và duyệt KYC trong một lần."
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
