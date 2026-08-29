using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Filters;

/// <summary>
/// Bảo đảm mọi thao tác của khách tạo ra công việc cần Admin xử lý
/// đều xuất hiện dưới dạng notification có hành động.
/// </summary>
public sealed class AdminWorkNotificationFilter : IAsyncActionFilter
{
    private readonly ApplicationDbContext _dbContext;

    public AdminWorkNotificationFilter(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        if (!context.HttpContext.User.IsInRole(RoleNames.Customer))
        {
            await next();
            return;
        }

        var startedAt = DateTime.UtcNow.AddSeconds(-3);
        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        var customerId =
            context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
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

        var customerName = await _dbContext.Users
            .Where(user => user.Id == customerId)
            .Select(user => user.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Khách hàng";

        // ExtensionService cũ tạo một notification thông tin.
        // Xóa đúng notification vừa tạo trong request này để tránh trùng.
        var legacyExtensionNotifications = await _dbContext.Notifications
            .Where(item =>
                adminIds.Contains(item.UserId) &&
                item.CreatedAt >= startedAt &&
                item.Title == "Có yêu cầu gia hạn thuê xe")
            .ToListAsync(cancellationToken);

        if (legacyExtensionNotifications.Count > 0)
        {
            _dbContext.Notifications.RemoveRange(legacyExtensionNotifications);
        }

        // Đơn thuê mới đang chờ Admin xác nhận.
        var pendingBookings = await _dbContext.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.CustomerId == customerId &&
                booking.Status == BookingStatus.PendingConfirmation)
            .Select(booking => new
            {
                booking.BookingId,
                booking.PickupDate,
                booking.ReturnDate,
                VehicleName = booking.Vehicle.VehicleName
            })
            .ToListAsync(cancellationToken);

        foreach (var booking in pendingBookings)
        {
            await EnsureWorkNotificationAsync(
                adminIds,
                $"Đơn thuê chờ xử lý|{booking.BookingId}",
                $"{customerName} vừa gửi đơn #{booking.BookingId} thuê {booking.VehicleName} " +
                $"từ {booking.PickupDate:dd/MM/yyyy HH:mm} đến {booking.ReturnDate:dd/MM/yyyy HH:mm}. " +
                "Hãy xác nhận hoặc từ chối đơn.",
                cancellationToken);
        }

        // Yêu cầu gia hạn.
        var pendingExtensions = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Where(extension =>
                extension.Booking.CustomerId == customerId &&
                extension.Status == BookingExtensionStatus.Pending)
            .Select(extension => new
            {
                extension.BookingExtensionId,
                extension.BookingId,
                extension.RequestedReturnDate,
                VehicleName = extension.Booking.Vehicle.VehicleName
            })
            .ToListAsync(cancellationToken);

        foreach (var extension in pendingExtensions)
        {
            await EnsureWorkNotificationAsync(
                adminIds,
                $"Yêu cầu gia hạn chờ xử lý|{extension.BookingExtensionId}",
                $"{customerName} yêu cầu gia hạn đơn #{extension.BookingId} - {extension.VehicleName} " +
                $"đến {extension.RequestedReturnDate:dd/MM/yyyy HH:mm}. Hãy duyệt hoặc từ chối yêu cầu.",
                cancellationToken);
        }

        // Khách báo đã chuyển khoản QR, cần Admin xác nhận.
        var pendingQrPayments = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Booking.CustomerId == customerId &&
                payment.Status == PaymentStatus.AwaitingConfirmation &&
                payment.Method == PaymentMethods.BankQr)
            .Select(payment => new
            {
                payment.PaymentId,
                payment.BookingId,
                payment.Amount,
                payment.Type,
                VehicleName = payment.Booking.Vehicle.VehicleName
            })
            .ToListAsync(cancellationToken);

        foreach (var payment in pendingQrPayments)
        {
            await EnsureWorkNotificationAsync(
                adminIds,
                $"Thanh toán QR chờ xác nhận|{payment.PaymentId}",
                $"{customerName} báo đã chuyển {payment.Amount:N0} đồng cho đơn #{payment.BookingId} " +
                $"- {payment.VehicleName}. Hãy kiểm tra và xác nhận thanh toán {payment.Type}.",
                cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureWorkNotificationAsync(
        IReadOnlyCollection<string> adminIds,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var existingAdminIds = await _dbContext.Notifications
            .AsNoTracking()
            .Where(item =>
                adminIds.Contains(item.UserId) &&
                !item.IsRead &&
                item.Title == title)
            .Select(item => item.UserId)
            .ToListAsync(cancellationToken);

        foreach (var adminId in adminIds.Except(existingAdminIds))
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            });
        }
    }
}
