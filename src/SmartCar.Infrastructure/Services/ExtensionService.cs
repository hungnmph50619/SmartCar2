using System.Data;
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
    private const string ForceMajeureMarker = "[FORCE_MAJEURE]";
    private const string EvidenceMarker = "[EVIDENCE]";
    private const string NoteMarker = "[NOTE]";

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
            .Where(extension =>
                extension.Booking.Status == BookingStatus.Rented &&
                (extension.Status == BookingExtensionStatus.Pending ||
                 extension.Status == BookingExtensionStatus.NeedsEvidence))
            .OrderBy(extension => extension.RequestedAt)
            .ToListAsync(cancellationToken);

        return await MapExtensionsAsync(extensions, cancellationToken);
    }

    public async Task<OperationResult> RequestAsync(
        string customerId,
        RequestExtensionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

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

        if (string.IsNullOrWhiteSpace(request.CustomerNote))
        {
            return OperationResult.Failure("Vui lòng nêu rõ lý do cần gia hạn.");
        }

        if (booking.Extensions.Any(extension =>
            extension.Status is BookingExtensionStatus.Pending
                or BookingExtensionStatus.NeedsEvidence
                or BookingExtensionStatus.Approved))
        {
            return OperationResult.Failure("Đơn đang có một yêu cầu gia hạn chưa hoàn tất.");
        }

        if (request.IsForceMajeure && string.IsNullOrWhiteSpace(request.EvidenceNote))
        {
            return OperationResult.Failure(
                "Trường hợp bất khả kháng cần mô tả minh chứng: tình trạng xe/sự cố và vị trí hiện tại.");
        }

        var conflict = await FindConflictAsync(
            booking.VehicleId,
            booking.BookingId,
            booking.ReturnDate,
            request.RequestedReturnDate,
            cancellationToken);

        if (conflict is not null && !request.IsForceMajeure)
        {
            return OperationResult.Failure(
                $"Không thể gia hạn thông thường đến {request.RequestedReturnDate:dd/MM/yyyy HH:mm}. " +
                $"Xe đã có đơn #{conflict.BookingId} bắt đầu lúc {conflict.PickupDate:dd/MM/yyyy HH:mm}. " +
                "Vui lòng trả xe đúng hạn. Nếu thực sự bất khả kháng, hãy chọn loại yêu cầu bất khả kháng và gửi minh chứng/vị trí để SmartCar xử lý riêng.");
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
            CustomerNote = BuildStoredCustomerNote(
                request.IsForceMajeure,
                request.CustomerNote,
                request.EvidenceNote),
            Status = BookingExtensionStatus.Pending,
            RequestedAt = DateTime.UtcNow
        });

        var adminTitle = request.IsForceMajeure
            ? conflict is null
                ? "Có yêu cầu gia hạn bất khả kháng"
                : "Gia hạn bất khả kháng đang xung đột lịch xe"
            : "Có yêu cầu gia hạn thuê xe";

        var adminMessage = conflict is null
            ? $"Đơn #{booking.BookingId} - {booking.Vehicle.VehicleName} đang chờ duyệt gia hạn."
            : $"Đơn #{booking.BookingId} xin gia hạn bất khả kháng nhưng xe đã có đơn #{conflict.BookingId} từ {conflict.PickupDate:dd/MM/yyyy HH:mm}. Cần xử lý khách/xe kế tiếp trước khi duyệt.";

        await NotifyAdminsAsync(adminTitle, adminMessage, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ApproveAsync(
        int extensionId,
        string adminId,
        bool confirmConflictHandled = false,
        string? adminNote = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

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

        var isForceMajeure = IsForceMajeure(extension.CustomerNote);
        var conflict = await FindConflictAsync(
            extension.Booking.VehicleId,
            extension.BookingId,
            extension.OriginalReturnDate,
            extension.RequestedReturnDate,
            cancellationToken);

        if (conflict is not null && !isForceMajeure)
        {
            return OperationResult.Failure(
                $"Không thể duyệt: xe đã được giữ cho đơn #{conflict.BookingId} từ {conflict.PickupDate:dd/MM/yyyy HH:mm}.");
        }

        if (conflict is not null && !confirmConflictHandled)
        {
            return OperationResult.Failure(
                $"Yêu cầu bất khả kháng đang xung đột đơn #{conflict.BookingId}. " +
                "Chỉ duyệt sau khi đã liên hệ và xử lý phương án xe/hoàn tiền cho khách kế tiếp, rồi đánh dấu xác nhận trên màn duyệt.");
        }

        extension.Status = BookingExtensionStatus.Approved;
        extension.AdminNote = string.IsNullOrWhiteSpace(adminNote)
            ? conflict is null
                ? "SmartCar đã duyệt yêu cầu. Thời gian trả mới chỉ có hiệu lực sau khi thanh toán gia hạn được xác nhận."
                : $"SmartCar đã duyệt sau khi quản trị viên xác nhận đã xử lý xung đột với đơn #{conflict.BookingId}. Thời gian trả mới chỉ có hiệu lực sau khi thanh toán."
            : adminNote.Trim();
        extension.DecidedAt = DateTime.UtcNow;

        var hasOpenExtensionPayment = extension.Booking.Payments.Any(payment =>
            payment.Type == PaymentType.Extension &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation);

        if (!hasOpenExtensionPayment)
        {
            extension.Booking.Payments.Add(new Payment
            {
                Type = PaymentType.Extension,
                Amount = extension.AdditionalAmount,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending
            });
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = extension.Booking.CustomerId,
            Title = "Yêu cầu gia hạn đã được duyệt",
            Message =
                $"Đơn #{extension.BookingId} được duyệt gia hạn đến {extension.RequestedReturnDate:dd/MM/yyyy HH:mm}. " +
                $"Vui lòng thanh toán {extension.AdditionalAmount:N0} đồng. Thời gian trả mới có hiệu lực sau khi SmartCar xác nhận khoản thanh toán này."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> RequestMoreEvidenceAsync(
        int extensionId,
        string adminId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Failure("Vui lòng nêu rõ minh chứng/thông tin cần khách bổ sung.");
        }

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        if (extension is null ||
            extension.Status != BookingExtensionStatus.Pending ||
            extension.Booking.Status != BookingStatus.Rented)
        {
            return OperationResult.Failure("Yêu cầu gia hạn không tồn tại hoặc không còn hợp lệ để yêu cầu bổ sung.");
        }

        extension.Status = BookingExtensionStatus.NeedsEvidence;
        extension.AdminNote = reason.Trim();
        extension.DecidedAt = DateTime.UtcNow;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = extension.Booking.CustomerId,
            Title = "Cần bổ sung minh chứng gia hạn",
            Message = $"Đơn #{extension.BookingId}: {extension.AdminNote} Vui lòng mở lịch sử gia hạn để bổ sung rồi gửi lại."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SupplementEvidenceAsync(
        int extensionId,
        string customerId,
        string evidenceNote,
        string? customerNote = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(evidenceNote))
        {
            return OperationResult.Failure("Vui lòng mô tả minh chứng/tình trạng và vị trí hiện tại.");
        }

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Vehicle)
            .FirstOrDefaultAsync(item =>
                item.BookingExtensionId == extensionId &&
                item.Booking.CustomerId == customerId,
                cancellationToken);

        if (extension is null)
        {
            return OperationResult.Failure("Không tìm thấy yêu cầu gia hạn của bạn.");
        }

        if (extension.Status != BookingExtensionStatus.NeedsEvidence ||
            extension.Booking.Status != BookingStatus.Rented)
        {
            return OperationResult.Failure("Yêu cầu gia hạn không còn hợp lệ để bổ sung minh chứng.");
        }

        var resolvedCustomerNote = string.IsNullOrWhiteSpace(customerNote)
            ? ExtractCustomerNote(extension.CustomerNote)
            : customerNote.Trim();

        if (string.IsNullOrWhiteSpace(resolvedCustomerNote))
        {
            return OperationResult.Failure("Vui lòng nêu rõ lý do cần gia hạn.");
        }

        extension.CustomerNote = BuildStoredCustomerNote(
            true,
            resolvedCustomerNote,
            evidenceNote);
        extension.Status = BookingExtensionStatus.Pending;
        extension.AdminNote = null;
        extension.DecidedAt = null;

        await NotifyAdminsAsync(
            "Khách đã bổ sung minh chứng gia hạn",
            $"Đơn #{extension.BookingId} - {extension.Booking.Vehicle.VehicleName} đã bổ sung minh chứng và gửi lại để duyệt.",
            cancellationToken);

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
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(reason))
        {
            return OperationResult.Failure("Vui lòng nhập lý do từ chối gia hạn.");
        }

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        if (extension is null ||
            extension.Status is not (BookingExtensionStatus.Pending or BookingExtensionStatus.NeedsEvidence))
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
            Message =
                $"Đơn #{extension.BookingId} không được gia hạn. Lý do: {extension.AdminNote} " +
                "Vui lòng trả xe theo thời hạn hiện tại; phí trả muộn/thiệt hại phát sinh áp dụng theo điều khoản đã xác nhận."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
            .Select(user => new { user.Id, user.FullName })
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        var result = new List<ExtensionDto>(extensions.Count);
        foreach (var extension in extensions)
        {
            var conflict = await FindConflictAsync(
                extension.Booking.VehicleId,
                extension.BookingId,
                extension.OriginalReturnDate,
                extension.RequestedReturnDate,
                cancellationToken);

            result.Add(new ExtensionDto(
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
                ExtractCustomerNote(extension.CustomerNote),
                extension.AdminNote,
                extension.RequestedAt,
                IsForceMajeure(extension.CustomerNote),
                ExtractEvidence(extension.CustomerNote),
                conflict is not null,
                conflict?.BookingId,
                conflict?.PickupDate));
        }

        return result;
    }

    private async Task<ConflictInfo?> FindConflictAsync(
        int vehicleId,
        int currentBookingId,
        DateTime currentReturnDate,
        DateTime requestedReturnDate,
        CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.Bookings
            .AsNoTracking()
            .Where(other =>
                other.VehicleId == vehicleId &&
                other.BookingId != currentBookingId &&
                BlockingStatuses.Contains(other.Status) &&
                other.ReturnDate > currentReturnDate)
            .OrderBy(other => other.PickupDate)
            .Select(other => new
            {
                other.BookingId,
                other.PickupDate,
                other.ReturnDate,
                other.PickupMethod,
                other.PolicyJson
            })
            .ToListAsync(cancellationToken);

        foreach (var other in candidates)
        {
            // Khoảng chuẩn bị trước lượt kế tiếp phải theo policy snapshot của chính
            // booking kế tiếp. Admin đổi cấu hình sau khi booking đã tạo không được
            // làm thay đổi khoảng xoay vòng đã chốt của booking đó.
            var otherPolicy = RentalPolicySnapshot.FromJson(other.PolicyJson);
            var preparationMinutes = otherPolicy.GetOperationalPreparationMinutes(
                other.PickupMethod);
            var requiredBoundary = requestedReturnDate.AddMinutes(preparationMinutes);

            if (other.PickupDate < requiredBoundary)
            {
                return new ConflictInfo(
                    other.BookingId,
                    other.PickupDate,
                    other.ReturnDate);
            }
        }

        return null;
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

    private static string? BuildStoredCustomerNote(
        bool isForceMajeure,
        string? customerNote,
        string? evidenceNote)
    {
        var note = Normalize(customerNote);
        if (!isForceMajeure)
        {
            return note;
        }

        var evidence = Normalize(evidenceNote) ?? string.Empty;
        return $"{ForceMajeureMarker}\n{EvidenceMarker}{evidence}\n{NoteMarker}{note ?? string.Empty}";
    }

    private static bool IsForceMajeure(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(ForceMajeureMarker, StringComparison.Ordinal);

    private static string? ExtractEvidence(string? value) =>
        ExtractTaggedValue(value, EvidenceMarker);

    private static string? ExtractCustomerNote(string? value) =>
        IsForceMajeure(value)
            ? ExtractTaggedValue(value, NoteMarker)
            : Normalize(value);

    private static string? ExtractTaggedValue(string? value, string marker)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var start = value.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = value.IndexOf('\n', start);
        var result = end < 0 ? value[start..] : value[start..end];
        return Normalize(result);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ConflictInfo(
        int BookingId,
        DateTime PickupDate,
        DateTime ReturnDate);
}