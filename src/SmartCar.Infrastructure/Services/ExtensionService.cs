using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class ExtensionService : IExtensionService
{
    private static readonly BookingStatus[] BlockingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;

    public ExtensionService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ExtensionDto>> GetCustomerExtensionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        var extensions = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Include(extension => extension.Booking)
                .ThenInclude(booking => booking.Vehicle)
            .Where(extension => extension.Booking.CustomerId == customerId)
            .OrderByDescending(extension => extension.RequestedAt)
            .ToListAsync(cancellationToken);

        return await MapExtensionsAsync(extensions, cancellationToken);
    }

    public async Task<IReadOnlyList<ExtensionDto>> GetPendingExtensionsAsync(
        CancellationToken cancellationToken = default)
    {
        var extensions = await _dbContext.BookingExtensions
            .AsNoTracking()
            .Include(extension => extension.Booking)
                .ThenInclude(booking => booking.Vehicle)
            .Where(extension => extension.Status == BookingExtensionStatus.Pending)
            .OrderBy(extension => extension.RequestedAt)
            .ToListAsync(cancellationToken);

        return await MapExtensionsAsync(extensions, cancellationToken);
    }

    public async Task<OperationResult> RequestAsync(
        string customerId,
        RequestExtensionRequest request,
        CancellationToken cancellationToken = default)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Extensions)
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item =>
                item.BookingId == request.BookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return OperationResult.Failure("Không tìm thấy đơn thuê của bạn.");
        }

        if (booking.Status != BookingStatus.Rented)
        {
            return OperationResult.Failure("Chỉ đơn đang thuê mới được yêu cầu gia hạn.");
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            return OperationResult.Failure("Đơn đã đến hạn trả xe, không thể yêu cầu gia hạn.");
        }

        if (request.RequestedReturnDate <= booking.ReturnDate)
        {
            return OperationResult.Failure("Thời gian trả mới phải sau thời gian trả hiện tại.");
        }

        if (booking.Extensions.Any(extension =>
            extension.Status is BookingExtensionStatus.Pending or BookingExtensionStatus.Approved))
        {
            return OperationResult.Failure("Đơn đang có một yêu cầu gia hạn chưa hoàn tất.");
        }

        var additionalDays = Math.Max(
            1,
            (int)Math.Ceiling((request.RequestedReturnDate - booking.ReturnDate).TotalHours / 24d));
        var additionalAmount = additionalDays * booking.DailyPrice;

        booking.Extensions.Add(new BookingExtension
        {
            OriginalReturnDate = booking.ReturnDate,
            RequestedReturnDate = request.RequestedReturnDate,
            AdditionalDays = additionalDays,
            AdditionalAmount = additionalAmount,
            CustomerNote = Normalize(request.CustomerNote),
            Status = BookingExtensionStatus.Pending,
            RequestedAt = DateTime.UtcNow
        });

        await NotifyAdminsAsync(
            "Có yêu cầu gia hạn thuê xe",
            $"Đơn #{booking.BookingId} - {booking.Vehicle.VehicleName} đang chờ duyệt gia hạn.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ApproveAsync(
        int extensionId,
        string adminId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .FirstOrDefaultAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        if (extension is null)
        {
            return OperationResult.Failure("Không tìm thấy yêu cầu gia hạn.");
        }

        if (extension.Status != BookingExtensionStatus.Pending ||
            extension.Booking.Status != BookingStatus.Rented)
        {
            return OperationResult.Failure("Yêu cầu gia hạn không còn hợp lệ để duyệt.");
        }

        var hasConflict = await _dbContext.Bookings.AnyAsync(other =>
            other.VehicleId == extension.Booking.VehicleId &&
            other.BookingId != extension.BookingId &&
            BlockingStatuses.Contains(other.Status) &&
            extension.OriginalReturnDate < other.ReturnDate &&
            extension.RequestedReturnDate > other.PickupDate,
            cancellationToken);

        if (hasConflict)
        {
            return OperationResult.Failure("Không thể gia hạn vì xe đã có lịch thuê kế tiếp bị trùng.");
        }

        extension.Status = BookingExtensionStatus.Approved;
        extension.AdminNote = $"Được duyệt bởi Quản trị viên {adminId}.";
        extension.DecidedAt = DateTime.UtcNow;

        extension.Booking.ReturnDate = extension.RequestedReturnDate;
        extension.Booking.NumberOfDays += extension.AdditionalDays;
        extension.Booking.RentalAmount += extension.AdditionalAmount;
        extension.Booking.TotalAmount += extension.AdditionalAmount;
        extension.Booking.Payments.Add(new Payment
        {
            Type = PaymentType.Extension,
            Amount = extension.AdditionalAmount,
            Method = "Mô phỏng",
            Status = PaymentStatus.Pending
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = extension.Booking.CustomerId,
            Title = "Yêu cầu gia hạn đã được duyệt",
            Message = $"Đơn #{extension.BookingId} được gia hạn đến {extension.RequestedReturnDate:dd/MM/yyyy HH:mm}. Vui lòng thanh toán {extension.AdditionalAmount:N0} đồng."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RejectAsync(
        int extensionId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Failure("Vui lòng nhập lý do từ chối gia hạn.");
        }

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        if (extension is null || extension.Status != BookingExtensionStatus.Pending)
        {
            return OperationResult.Failure("Yêu cầu gia hạn không tồn tại hoặc đã được xử lý.");
        }

        extension.Status = BookingExtensionStatus.Rejected;
        extension.AdminNote = reason.Trim();
        extension.DecidedAt = DateTime.UtcNow;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = extension.Booking.CustomerId,
            Title = "Yêu cầu gia hạn bị từ chối",
            Message = $"Đơn #{extension.BookingId} không được gia hạn. Lý do: {extension.AdminNote}"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> MarkPaidAsync(
        int bookingId,
        CancellationToken cancellationToken = default)
    {
        var extension = await _dbContext.BookingExtensions
            .Where(item =>
                item.BookingId == bookingId &&
                item.Status == BookingExtensionStatus.Approved)
            .OrderByDescending(item => item.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (extension is null)
        {
            return OperationResult.Failure("Không tìm thấy gia hạn đang chờ thanh toán.");
        }

        extension.Status = BookingExtensionStatus.Paid;
        extension.PaidAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    private async Task<IReadOnlyList<ExtensionDto>> MapExtensionsAsync(
        IReadOnlyCollection<BookingExtension> extensions,
        CancellationToken cancellationToken)
    {
        if (extensions.Count == 0)
        {
            return Array.Empty<ExtensionDto>();
        }

        var customerIds = extensions
            .Select(extension => extension.Booking.CustomerId)
            .Distinct()
            .ToList();

        var customerNames = await _dbContext.Users
            .AsNoTracking()
            .Where(user => customerIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.FullName
            })
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        return extensions
            .Select(extension => new ExtensionDto(
                extension.BookingExtensionId,
                extension.BookingId,
                extension.Booking.CustomerId,
                customerNames.GetValueOrDefault(extension.Booking.CustomerId) ?? string.Empty,
                extension.Booking.Vehicle.VehicleName,
                extension.OriginalReturnDate,
                extension.RequestedReturnDate,
                extension.AdditionalDays,
                extension.AdditionalAmount,
                extension.Status,
                extension.CustomerNote,
                extension.AdminNote,
                extension.RequestedAt))
            .ToList();
    }

    private async Task NotifyAdminsAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var roleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(roleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .Where(item => item.RoleId == roleId)
            .Select(item => item.UserId)
            .ToListAsync(cancellationToken);

        foreach (var adminId in adminIds)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            });
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
