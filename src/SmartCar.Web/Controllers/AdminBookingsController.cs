using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminBookingsController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IDocumentService _documentService;

    public AdminBookingsController(
        IBookingService bookingService,
        IAuditService auditService,
        ApplicationDbContext dbContext,
        IDocumentService documentService)
    {
        _bookingService = bookingService;
        _auditService = auditService;
        _dbContext = dbContext;
        _documentService = documentService;
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
        if (string.IsNullOrWhiteSpace(query))
        {
            return View(bookings);
        }

        var keyword = query.Trim();
        var bookingIdText = keyword.TrimStart('#');
        var hasBookingId = int.TryParse(bookingIdText, out var bookingId);

        return View(bookings
            .Where(booking =>
                (hasBookingId && booking.BookingId == bookingId) ||
                booking.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(booking.CustomerPhone) &&
                 booking.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                booking.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                booking.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(id, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        // Biên bản giao đã lập nhưng chưa ký: đây là bước bắt buộc tiếp theo.
        // Đưa thẳng Admin tới bản in để tránh hiện lại nút "Lập biên bản".
        if (booking.Status == BookingStatus.ReadyForPickup && booking.HasHandover)
        {
            return RedirectToAction(
                "HandoverPrint",
                "AdminRentalDocuments",
                new { bookingId = id });
        }

        var customerCreatedAt = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == booking.CustomerId)
            .Select(user => (DateTime?)user.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var customerBookingStatuses = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.CustomerId == booking.CustomerId)
            .Select(item => item.Status)
            .ToListAsync(cancellationToken);

        var documents = await _documentService.GetCustomerDocumentsAsync(
            booking.CustomerId,
            cancellationToken);

        var citizenFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
        var citizenBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenIdBack);
        var drivingLicense = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);
        var drivingLicenseBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicenseBack);

        var citizenVerified = citizenFront is not null &&
                              citizenBack is not null &&
                              citizenFront.Status == DocumentStatus.Verified &&
                              citizenBack.Status == DocumentStatus.Verified &&
                              citizenFront.HasRequiredData &&
                              citizenBack.HasRequiredData &&
                              citizenFront.ExpiryDate.HasValue &&
                              citizenFront.ExpiryDate.Value.Date >= booking.ReturnDate.Date;

        var drivingLicenseVerified = drivingLicense is not null &&
                                     drivingLicenseBack is not null &&
                                     drivingLicense.Status == DocumentStatus.Verified &&
                                     drivingLicenseBack.Status == DocumentStatus.Verified &&
                                     drivingLicense.HasRequiredData &&
                                     drivingLicenseBack.HasRequiredData &&
                                     drivingLicense.ExpiryDate.HasValue &&
                                     drivingLicense.ExpiryDate.Value.Date >= booking.ReturnDate.Date;

        var tripDocuments = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == id, cancellationToken);

        var vehicleStatus = await _dbContext.Vehicles
            .AsNoTracking()
            .Where(vehicle => vehicle.VehicleId == booking.VehicleId)
            .Select(vehicle => (VehicleStatus?)vehicle.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var priorBlockingStatuses = new[]
        {
            BookingStatus.PendingConfirmation,
            BookingStatus.PendingPayment,
            BookingStatus.Paid,
            BookingStatus.ReadyForPickup,
            BookingStatus.Rented,
            BookingStatus.PendingInspection
        };

        var previousActiveBooking = booking.Status == BookingStatus.Paid
            ? await _dbContext.Bookings
                .AsNoTracking()
                .Where(item =>
                    item.VehicleId == booking.VehicleId &&
                    item.BookingId != booking.BookingId &&
                    item.PickupDate < booking.PickupDate &&
                    priorBlockingStatuses.Contains(item.Status))
                .OrderByDescending(item => item.PickupDate)
                .Select(item => new
                {
                    item.BookingId,
                    item.Status,
                    item.ReturnDate
                })
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var canMarkReady = booking.Status == BookingStatus.Paid &&
                           vehicleStatus == VehicleStatus.Available &&
                           previousActiveBooking is null;

        string? readyBlockedReason = null;
        if (booking.Status == BookingStatus.Paid && !canMarkReady)
        {
            if (previousActiveBooking is not null)
            {
                var previousStatusText = previousActiveBooking.Status switch
                {
                    BookingStatus.Rented => "đang được thuê",
                    BookingStatus.PendingInspection => "đang chờ kiểm tra sau khi trả",
                    BookingStatus.ReadyForPickup => "đang chờ bàn giao",
                    BookingStatus.Paid => "đã thanh toán và đang chờ chuẩn bị",
                    BookingStatus.PendingPayment => "đang chờ thanh toán",
                    BookingStatus.PendingConfirmation => "đang chờ xác nhận",
                    _ => "chưa hoàn tất"
                };

                readyBlockedReason =
                    $"Đang chờ xe từ đơn #{previousActiveBooking.BookingId}. Đơn trước {previousStatusText}, " +
                    $"dự kiến trả {previousActiveBooking.ReturnDate:dd/MM/yyyy HH:mm}. " +
                    "Chỉ xác nhận sẵn sàng sau khi xe được trả và hoàn tất kiểm tra.";
            }
            else
            {
                readyBlockedReason = vehicleStatus switch
                {
                    VehicleStatus.Rented => "Xe vẫn đang được khách trước sử dụng. Chỉ xác nhận sẵn sàng sau khi xe được trả và hoàn tất kiểm tra.",
                    VehicleStatus.Inspection => "Xe đã được trả nhưng đang chờ hoàn tất kiểm tra. Chỉ xác nhận sẵn sàng khi xe trở lại trạng thái Có sẵn.",
                    VehicleStatus.Maintenance => "Xe đang bảo trì nên chưa thể chuẩn bị cho đơn này.",
                    VehicleStatus.OutOfService => "Xe đang ngừng hoạt động nên chưa thể chuẩn bị cho đơn này.",
                    _ => "Xe hiện chưa ở trạng thái Có sẵn nên chưa thể xác nhận sẵn sàng."
                };
            }
        }

        ViewBag.CustomerCreatedAt = customerCreatedAt;
        ViewBag.CustomerTotalBookingCount = customerBookingStatuses.Count;
        ViewBag.CustomerCompletedBookingCount = customerBookingStatuses.Count(status => status == BookingStatus.Completed);
        ViewBag.CustomerCancelledBookingCount = customerBookingStatuses.Count(status =>
            status is BookingStatus.Cancelled or BookingStatus.Rejected);
        ViewBag.CustomerNoShowCount = customerBookingStatuses.Count(status => status == BookingStatus.NoShow);
        ViewBag.CitizenIdentityVerified = citizenVerified;
        ViewBag.DrivingLicenseVerified = drivingLicenseVerified;
        ViewBag.CustomerKycVerified = citizenVerified && drivingLicenseVerified;
        ViewBag.HandoverSigned = HasSignedCopy(tripDocuments?.Handover?.ImagePaths, HandoverSignedMarker);
        ViewBag.ReturnSigned = HasSignedCopy(tripDocuments?.VehicleReturn?.ImagePaths, ReturnSignedMarker);
        ViewBag.CanMarkReady = canMarkReady;
        ViewBag.ReadyBlockedReason = readyBlockedReason;

        return View(booking);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id, CancellationToken cancellationToken)
    {
        var result = await _bookingService.ConfirmAsync(id, cancellationToken);
        SetMessage(result, "Đã xác nhận đơn thuê và tạo khoản thanh toán.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "Confirm",
                id,
                $"Xác nhận đơn thuê #{id} và chuyển sang chờ thanh toán.",
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
                "Reject",
                model.BookingId,
                $"Từ chối đơn thuê #{model.BookingId}. Lý do: {model.Reason.Trim()}",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(int id, CancellationToken cancellationToken)
    {
        var result = await _bookingService.MarkReadyForPickupAsync(id, cancellationToken);
        SetMessage(result, "Đã đánh dấu xe sẵn sàng bàn giao.");

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "MarkReady",
                id,
                $"Đánh dấu xe của đơn #{id} đã sẵn sàng bàn giao.",
                cancellationToken);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private static bool HasSignedCopy(string? paths, string marker) =>
        !string.IsNullOrWhiteSpace(paths) &&
        paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private void SetMessage(OperationResult result, string successMessage)
    {
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? successMessage
            : string.Join("; ", result.Errors);
    }

    private Task WriteAuditAsync(
        string action,
        int bookingId,
        string description,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return _auditService.WriteAsync(
            adminId,
            action,
            nameof(Booking),
            bookingId.ToString(),
            description,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }
}
