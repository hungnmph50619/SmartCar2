using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBookingOperationsController : Controller
{
    private static readonly string[] AllowedTerminationReasons =
    {
        "CustomerViolation",
        "VehicleSafety",
        "MutualAgreement",
        "Other"
    };

    private readonly IBookingOperationService _operationService;
    private readonly ApplicationDbContext _dbContext;

    public AdminBookingOperationsController(
        IBookingOperationService operationService,
        ApplicationDbContext dbContext)
    {
        _operationService = operationService;
        _dbContext = dbContext;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(
        CancelBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = ModelState.IsValid
            ? await _operationService.CancelByAdminAsync(
                adminId,
                new CancelBookingRequest(model.BookingId, model.Reason),
                cancellationToken)
            : RefundResult.Failure("Vui lòng nhập lý do hủy đơn.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? $"Đã hủy đơn và hoàn {result.RefundAmount:N0} đồng cho khách."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    [HttpGet]
    public async Task<IActionResult> PreparePickup(
        int bookingId,
        CancellationToken cancellationToken)
    {
        if (!await HasVehiclePreparedAsync(bookingId, cancellationToken))
        {
            TempData["ErrorMessage"] =
                "Xe chưa được ghi nhận chuẩn bị xong. Hãy hoàn tất kiểm tra/chuẩn bị xe trước khi gọi khách xác nhận giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var preparation = await _operationService.GetPickupPreparationAsync(
            bookingId,
            cancellationToken);

        if (preparation is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn đã thanh toán để chuẩn bị giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View("PreparePickup", preparation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmPickupDeparture(
        int bookingId,
        bool customerConfirmed,
        string contactNote,
        CancellationToken cancellationToken)
    {
        if (!await HasVehiclePreparedAsync(bookingId, cancellationToken))
        {
            TempData["ErrorMessage"] =
                "Không thể xác nhận xuất phát vì chưa có bằng chứng xe đã được chuẩn bị xong.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.ConfirmPickupDepartureAsync(
            new ConfirmPickupDepartureRequest(
                bookingId,
                customerConfirmed,
                contactNote ?? string.Empty),
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã lưu xác nhận của khách trước khi xuất phát. Đơn chuyển sang Sẵn sàng giao xe."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveEarlyReturn(
        int bookingId,
        string? decisionNote,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở trạng thái Đang thuê để duyệt trả xe sớm.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var latest = await GetLatestEarlyReturnAsync(bookingId, cancellationToken);
        if (latest is null || latest.Action != RentalLifecycleAuditHelper.EarlyReturnRequested)
        {
            TempData["ErrorMessage"] = "Không có yêu cầu trả xe sớm đang chờ duyệt.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var request = RentalLifecycleAuditHelper.ParseEarlyReturn(
            latest.Action,
            latest.NewValues,
            latest.CreatedAt.ToLocalTime());
        if (request is null)
        {
            TempData["ErrorMessage"] = "Dữ liệu yêu cầu trả sớm không hợp lệ.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var normalizedNote = decisionNote?.Trim() ?? string.Empty;
        if (normalizedNote.Length > 500)
        {
            TempData["ErrorMessage"] = "Ghi chú duyệt không được vượt quá 500 ký tự.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = adminId,
            Action = RentalLifecycleAuditHelper.EarlyReturnApproved,
            EntityName = nameof(Booking),
            EntityId = bookingId.ToString(),
            Description =
                $"Admin chấp thuận yêu cầu trả sớm đơn #{bookingId}: {request.RequestedReturnAt:dd/MM/yyyy HH:mm} tại {request.ReturnLocation}. " +
                "Booking vẫn giữ Rented cho đến khi xe thực tế được nhận lại và lập biên bản trả xe.",
            NewValues = RentalLifecycleAuditHelper.SerializeEarlyReturn(
                request.RequestedReturnAt,
                request.ReturnLocation,
                request.Reason,
                normalizedNote),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Yêu cầu trả xe sớm đã được chấp thuận",
            Message =
                $"Đơn #{bookingId}: SmartCar đồng ý tiếp nhận xe lúc {request.RequestedReturnAt:dd/MM/yyyy HH:mm} tại {request.ReturnLocation}. " +
                "Xe vẫn thuộc trách nhiệm của bạn cho đến khi SmartCar thực tế nhận lại xe, chìa khóa và hoàn tất biên bản trả xe. " +
                (string.IsNullOrWhiteSpace(normalizedNote) ? string.Empty : $"Ghi chú: {normalizedNote}")
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] =
            "Đã chấp thuận trả xe sớm. Đơn vẫn là Đang thuê cho đến khi tiếp nhận xe thực tế.";
        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectEarlyReturn(
        int bookingId,
        string decisionNote,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở trạng thái Đang thuê để xử lý yêu cầu trả sớm.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var normalizedNote = decisionNote?.Trim() ?? string.Empty;
        if (normalizedNote.Length < 5 || normalizedNote.Length > 500)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do từ chối từ 5 đến 500 ký tự.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var latest = await GetLatestEarlyReturnAsync(bookingId, cancellationToken);
        if (latest is null || latest.Action != RentalLifecycleAuditHelper.EarlyReturnRequested)
        {
            TempData["ErrorMessage"] = "Không có yêu cầu trả xe sớm đang chờ duyệt.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var request = RentalLifecycleAuditHelper.ParseEarlyReturn(
            latest.Action,
            latest.NewValues,
            latest.CreatedAt.ToLocalTime());
        if (request is null)
        {
            TempData["ErrorMessage"] = "Dữ liệu yêu cầu trả sớm không hợp lệ.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = adminId,
            Action = RentalLifecycleAuditHelper.EarlyReturnRejected,
            EntityName = nameof(Booking),
            EntityId = bookingId.ToString(),
            Description = $"Admin từ chối yêu cầu trả sớm đơn #{bookingId}. Lý do: {normalizedNote}",
            NewValues = RentalLifecycleAuditHelper.SerializeEarlyReturn(
                request.RequestedReturnAt,
                request.ReturnLocation,
                request.Reason,
                normalizedNote),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Yêu cầu trả xe sớm chưa được chấp thuận",
            Message =
                $"Đơn #{bookingId}: SmartCar chưa thể tiếp nhận xe theo yêu cầu trả sớm. Lý do: {normalizedNote}. " +
                $"Thời gian trả xe theo hợp đồng vẫn là {booking.ReturnDate:dd/MM/yyyy HH:mm}, trừ khi hai bên thống nhất yêu cầu mới."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã từ chối yêu cầu trả xe sớm và thông báo cho khách.";
        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEarlyTermination(
        int bookingId,
        string reasonType,
        DateTime requestedReturnAt,
        string reason,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang thuê mới có thể tạo yêu cầu chấm dứt/thu hồi xe trước hạn.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var normalizedType = reasonType?.Trim() ?? string.Empty;
        var normalizedReason = reason?.Trim() ?? string.Empty;

        if (!AllowedTerminationReasons.Contains(normalizedType, StringComparer.Ordinal))
        {
            TempData["ErrorMessage"] = "Loại lý do chấm dứt thuê không hợp lệ.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (requestedReturnAt < DateTime.Now || requestedReturnAt >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Thời hạn yêu cầu trả xe phải từ thời điểm hiện tại và sớm hơn thời gian trả xe theo hợp đồng.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (normalizedReason.Length < 10 || normalizedReason.Length > 1000)
        {
            TempData["ErrorMessage"] = "Vui lòng mô tả căn cứ chấm dứt/thu hồi từ 10 đến 1000 ký tự.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var latestTermination = await GetLatestTerminationAsync(bookingId, cancellationToken);
        if (latestTermination?.Action == RentalLifecycleAuditHelper.RentalTerminationRequested)
        {
            TempData["ErrorMessage"] = "Đơn đã có yêu cầu chấm dứt/thu hồi xe đang hiệu lực.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var reasonLabel = RentalLifecycleAuditHelper.ToVietnameseTerminationReason(normalizedType);
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = adminId,
            Action = RentalLifecycleAuditHelper.RentalTerminationRequested,
            EntityName = nameof(Booking),
            EntityId = bookingId.ToString(),
            Description =
                $"Admin yêu cầu chấm dứt thuê trước hạn đơn #{bookingId}. Nhóm lý do: {reasonLabel}. " +
                $"Yêu cầu trả xe trước {requestedReturnAt:dd/MM/yyyy HH:mm}. Căn cứ: {normalizedReason}. " +
                "Booking vẫn giữ Rented cho đến khi xe thực tế quay về.",
            NewValues = RentalLifecycleAuditHelper.SerializeTermination(
                normalizedType,
                normalizedReason,
                requestedReturnAt),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "SmartCar yêu cầu kết thúc chuyến thuê trước hạn",
            Message =
                $"Đơn #{bookingId}: SmartCar yêu cầu phối hợp trả xe trước {requestedReturnAt:dd/MM/yyyy HH:mm}. " +
                $"Lý do: {reasonLabel}. Chi tiết: {normalizedReason}. " +
                "Đơn vẫn là Đang thuê và trách nhiệm đối với xe chỉ kết thúc sau khi SmartCar thực tế nhận lại xe và lập biên bản. " +
                (normalizedType == "VehicleSafety"
                    ? "Trường hợp liên quan an toàn/kỹ thuật sẽ được SmartCar xử lý quyền lợi phần thời gian chưa sử dụng sau khi tiếp nhận xe."
                    : string.Empty)
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] =
            "Đã tạo yêu cầu chấm dứt thuê trước hạn và thông báo khách. Trạng thái vẫn là Đang thuê cho đến khi xe thực tế được nhận lại.";
        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelEarlyTermination(
        int bookingId,
        string cancellationReason,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở trạng thái Đang thuê để rút yêu cầu thu hồi.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var normalizedReason = cancellationReason?.Trim() ?? string.Empty;
        if (normalizedReason.Length < 5 || normalizedReason.Length > 500)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do rút yêu cầu từ 5 đến 500 ký tự.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var latestTermination = await GetLatestTerminationAsync(bookingId, cancellationToken);
        if (latestTermination is null ||
            latestTermination.Action != RentalLifecycleAuditHelper.RentalTerminationRequested)
        {
            TempData["ErrorMessage"] = "Không có yêu cầu thu hồi đang hiệu lực để rút.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var request = RentalLifecycleAuditHelper.ParseTermination(
            latestTermination.Action,
            latestTermination.NewValues,
            latestTermination.CreatedAt.ToLocalTime());
        if (request is null)
        {
            TempData["ErrorMessage"] = "Dữ liệu yêu cầu thu hồi hiện tại không hợp lệ.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = adminId,
            Action = RentalLifecycleAuditHelper.RentalTerminationCancelled,
            EntityName = nameof(Booking),
            EntityId = bookingId.ToString(),
            Description = $"Admin rút yêu cầu chấm dứt/thu hồi xe trước hạn của đơn #{bookingId}. Lý do: {normalizedReason}",
            NewValues = RentalLifecycleAuditHelper.SerializeTermination(
                request.ReasonType,
                $"Yêu cầu trước: {request.Reason}. Lý do rút: {normalizedReason}",
                request.RequestedReturnAt),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "SmartCar đã rút yêu cầu trả xe trước hạn",
            Message =
                $"Đơn #{bookingId}: SmartCar đã rút yêu cầu chấm dứt thuê/thu hồi xe trước hạn. " +
                $"Lý do: {normalizedReason}. Lịch thuê hiện tại tiếp tục có hiệu lực."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã rút yêu cầu thu hồi trước hạn và thông báo cho khách.";
        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNoShow(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var preparation = await _operationService.GetNoShowPreparationAsync(
            bookingId,
            cancellationToken);

        if (preparation is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê để xử lý khách không đến nhận xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (DateTime.Now < preparation.PickupDate.AddMinutes(30))
        {
            TempData["ErrorMessage"] =
                "Chưa đủ 30 phút kể từ giờ nhận xe. Hãy tiếp tục liên hệ khách trước khi ghi nhận NoShow.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View("ConfirmNoShow", preparation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmNoShow(
        int bookingId,
        int contactAttemptCount,
        bool arrivedAtPickupLocation,
        string contactNote,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _operationService.MarkNoShowAsync(
            new MarkNoShowRequest(
                bookingId,
                contactAttemptCount,
                arrivedAtPickupLocation,
                contactNote ?? string.Empty),
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận NoShow sau khi xác minh đủ các bước. Hệ thống đã tính phí NoShow và tạo khoản hoàn tiền nếu còn số dư phải hoàn."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    private Task<AuditLog?> GetLatestEarlyReturnAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var bookingIdText = bookingId.ToString();
        return _dbContext.AuditLogs
            .AsNoTracking()
            .Where(item =>
                item.EntityName == nameof(Booking) &&
                item.EntityId == bookingIdText &&
                RentalLifecycleAuditHelper.EarlyReturnActions.Contains(item.Action))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.AuditLogId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private Task<AuditLog?> GetLatestTerminationAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var bookingIdText = bookingId.ToString();
        return _dbContext.AuditLogs
            .AsNoTracking()
            .Where(item =>
                item.EntityName == nameof(Booking) &&
                item.EntityId == bookingIdText &&
                RentalLifecycleAuditHelper.RentalTerminationActions.Contains(item.Action))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.AuditLogId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private Task<bool> HasVehiclePreparedAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var bookingIdText = bookingId.ToString();
        return _dbContext.AuditLogs
            .AsNoTracking()
            .AnyAsync(log =>
                log.Action == "VehiclePrepared" &&
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingIdText,
                cancellationToken);
    }
}
