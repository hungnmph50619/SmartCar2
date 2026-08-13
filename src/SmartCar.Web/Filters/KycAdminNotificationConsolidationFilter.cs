using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Filters;

/// <summary>
/// Mỗi lần khách gửi CCCD/GPLX, Admin nhận đúng một work notification.
/// Nếu sau đó hồ sơ đã đủ cả CCCD + GPLX thì các notification rời rạc được
/// gộp thành một work item "Hồ sơ KYC chờ duyệt".
/// </summary>
public sealed class KycAdminNotificationConsolidationFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> CustomerKycSubmitActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SubmitCitizenId",
            "SubmitDrivingLicense",
            "VerifyAndSubmitCitizenId"
        };

    private static readonly string[] LegacyKycNotificationTitles =
    {
        "Có CCCD chờ xác minh",
        "Có GPLX chờ xác minh",
        "Có giấy tờ chờ xác minh",
        "CCCD cập nhật chờ duyệt",
        "GPLX cập nhật chờ duyệt"
    };

    private readonly ApplicationDbContext _dbContext;

    public KycAdminNotificationConsolidationFilter(
        ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var action =
            context.ActionDescriptor.RouteValues.TryGetValue(
                "action",
                out var actionName)
                ? actionName
                : null;

        if (string.IsNullOrWhiteSpace(action) ||
            !CustomerKycSubmitActions.Contains(action) ||
            !context.HttpContext.User.IsInRole(RoleNames.Customer))
        {
            await next();
            return;
        }

        var startedAt = DateTime.UtcNow.AddSeconds(-3);
        var executed = await next();

        if (executed.Exception is not null &&
            !executed.ExceptionHandled)
        {
            return;
        }

        var customerId =
            context.HttpContext.User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return;
        }

        var cancellationToken =
            context.HttpContext.RequestAborted;

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

        // Bỏ notification thông tin cũ vừa phát sinh trong request này.
        // Chỉ giữ work notification có CTA ở bên dưới.
        var legacyNotifications =
            await _dbContext.Notifications
                .Where(item =>
                    adminIds.Contains(item.UserId) &&
                    item.CreatedAt >= startedAt &&
                    LegacyKycNotificationTitles.Contains(item.Title))
                .ToListAsync(cancellationToken);

        if (legacyNotifications.Count > 0)
        {
            _dbContext.Notifications.RemoveRange(
                legacyNotifications);
        }

        var requiredTypes = new[]
        {
            DocumentTypes.CitizenId,
            DocumentTypes.CitizenIdBack,
            DocumentTypes.DrivingLicense,
            DocumentTypes.DrivingLicenseBack
        };

        var documents =
            await _dbContext.CustomerDocuments
                .AsNoTracking()
                .Where(document =>
                    document.CustomerId == customerId &&
                    requiredTypes.Contains(document.DocumentType))
                .Select(document => new
                {
                    document.DocumentType,
                    document.Status
                })
                .ToListAsync(cancellationToken);

        var citizenPending = documents.Any(document =>
            (document.DocumentType == DocumentTypes.CitizenId ||
             document.DocumentType == DocumentTypes.CitizenIdBack) &&
            document.Status == DocumentStatus.Pending);

        var licensePending = documents.Any(document =>
            (document.DocumentType == DocumentTypes.DrivingLicense ||
             document.DocumentType == DocumentTypes.DrivingLicenseBack) &&
            document.Status == DocumentStatus.Pending);

        var completePendingPackage =
            requiredTypes.All(type =>
                documents.Any(document =>
                    document.DocumentType == type &&
                    document.Status == DocumentStatus.Pending));

        var customerName =
            await _dbContext.Users
                .Where(user => user.Id == customerId)
                .Select(user => user.FullName)
                .FirstOrDefaultAsync(cancellationToken)
            ?? "Khách hàng";

        if (completePendingPackage)
        {
            // Đã đủ cả 2 loại giấy tờ: xóa work item rời để Admin chỉ thấy 1 việc.
            var partialTitles = new[]
            {
                $"CCCD chờ xác minh|{customerId}",
                $"GPLX chờ xác minh|{customerId}"
            };

            var partialWork =
                await _dbContext.Notifications
                    .Where(item =>
                        adminIds.Contains(item.UserId) &&
                        partialTitles.Contains(item.Title))
                    .ToListAsync(cancellationToken);

            if (partialWork.Count > 0)
            {
                _dbContext.Notifications.RemoveRange(partialWork);
            }

            await EnsureAdminWorkAsync(
                adminIds,
                $"Hồ sơ KYC chờ duyệt|{customerId}",
                $"{customerName} đã gửi đủ CCCD và GPLX. " +
                "Hãy mở hồ sơ để đối chiếu cả hai giấy tờ và duyệt hồ sơ trong một lần.",
                cancellationToken);
        }
        else if (
            (string.Equals(
                 action,
                 "SubmitCitizenId",
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(
                 action,
                 "VerifyAndSubmitCitizenId",
                 StringComparison.OrdinalIgnoreCase)) &&
            citizenPending)
        {
            await EnsureAdminWorkAsync(
                adminIds,
                $"CCCD chờ xác minh|{customerId}",
                $"{customerName} vừa gửi CCCD mới. " +
                "Hãy kiểm tra thông tin và hai mặt CCCD để xác minh.",
                cancellationToken);
        }
        else if (
            string.Equals(
                action,
                "SubmitDrivingLicense",
                StringComparison.OrdinalIgnoreCase) &&
            licensePending)
        {
            await EnsureAdminWorkAsync(
                adminIds,
                $"GPLX chờ xác minh|{customerId}",
                $"{customerName} vừa gửi GPLX mới. " +
                "Hãy kiểm tra thông tin và hai mặt GPLX để xác minh.",
                cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureAdminWorkAsync(
        IReadOnlyCollection<string> adminIds,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var existingAdminIds =
            await _dbContext.Notifications
                .AsNoTracking()
                .Where(item =>
                    adminIds.Contains(item.UserId) &&
                    !item.IsRead &&
                    item.Title == title)
                .Select(item => item.UserId)
                .ToListAsync(cancellationToken);

        foreach (var adminId in
                 adminIds.Except(existingAdminIds))
        {
            _dbContext.Notifications.Add(
                new Notification
                {
                    UserId = adminId,
                    Title = title,
                    Message = message
                });
        }
    }
}
