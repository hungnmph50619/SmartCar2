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
    private readonly IBookingService _bookingService;
    private readonly IDeliveryQuoteService _deliveryQuoteService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IDocumentService _documentService;

    public AdminBookingsController(
        IBookingService bookingService,
        IDeliveryQuoteService deliveryQuoteService,
        IAuditService auditService,
        ApplicationDbContext dbContext,
        IDocumentService documentService)
    {
        _bookingService = bookingService;
        _deliveryQuoteService = deliveryQuoteService;
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

        var filteredBookings = bookings
            .Where(booking =>
                (hasBookingId && booking.BookingId == bookingId) ||
                booking.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(booking.CustomerPhone) &&
                 booking.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                booking.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                booking.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return View(filteredBookings);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(id, cancellationToken);
        if (booking is null)
        {
            return NotFound();
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

        ViewBag.CustomerCreatedAt = customerCreatedAt;
        ViewBag.CustomerTotalBookingCount = customerBookingStatuses.Count;
        ViewBag.CustomerCompletedBookingCount = customerBookingStatuses.Count(status => status == BookingStatus.Completed);
        ViewBag.CustomerCancelledBookingCount = customerBookingStatuses.Count(status =>
            status is BookingStatus.Cancelled or BookingStatus.Rejected);
        ViewBag.CustomerNoShowCount = customerBookingStatuses.Count(status => status == BookingStatus.NoShow);
        ViewBag.CitizenIdentityVerified = citizenVerified;
        ViewBag.DrivingLicenseVerified = drivingLicenseVerified;
        ViewBag.CustomerKycVerified = citizenVerified && drivingLicenseVerified;

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
    public async Task<IActionResult> UpdateLocations(
        int bookingId,
        string pickupLocation,
        string returnLocation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pickupLocation) ||
            string.IsNullOrWhiteSpace(returnLocation))
        {
            TempData["ErrorMessage"] = "Địa điểm nhận xe và địa điểm trả xe không được để trống.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        pickupLocation = pickupLocation.Trim();
        returnLocation = returnLocation.Trim();

        if (pickupLocation.Length > 250 || returnLocation.Length > 250)
        {
            TempData["ErrorMessage"] = "Địa điểm nhận xe và địa điểm trả xe tối đa 250 ký tự.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status is not (BookingStatus.PendingConfirmation or BookingStatus.PendingPayment))
        {
            TempData["ErrorMessage"] =
                "Chỉ được thay đổi địa điểm giao nhận trước khi khách thanh toán. Sau thanh toán, hai bên cần xử lý thay đổi theo quy trình riêng.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var quote = await _deliveryQuoteService.CalculateAsync(
            booking.PickupMethod,
            pickupLocation,
            returnLocation,
            cancellationToken);

        if (!quote.Succeeded)
        {
            TempData["ErrorMessage"] = quote.Error ?? "Không thể tính lại phí giao nhận cho địa điểm mới.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        booking.PickupLocation = quote.PickupLocation;
        booking.ReturnLocation = quote.ReturnLocation;
        booking.PickupDeliveryDistanceKm = quote.PickupDeliveryDistanceKm;
        booking.ReturnCollectionDistanceKm = quote.ReturnCollectionDistanceKm;
        booking.DeliveryRatePerKm = quote.DeliveryRatePerKm;
        booking.DeliveryFee = quote.DeliveryFee;
        booking.TotalAmount = booking.RentalAmount + booking.DeliveryFee + booking.AdditionalAmount;

        var pendingRentalPayment = booking.Payments.FirstOrDefault(payment =>
            payment.Type == PaymentType.Rental &&
            payment.Status == PaymentStatus.Pending);
        if (pendingRentalPayment is not null)
        {
            pendingRentalPayment.Amount = booking.RentalAmount + booking.DeliveryFee;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Địa điểm và phí giao nhận đã được cập nhật",
            Message =
                $"Đơn #{booking.BookingId}: nhận xe tại {booking.PickupLocation}; " +
                $"trả xe tại {booking.ReturnLocation}; phí giao nhận {booking.DeliveryFee:N0} đồng."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        await WriteAuditAsync(
            "UpdateBookingLocations",
            bookingId,
            $"Cập nhật địa điểm giao nhận đơn #{bookingId}. Nhận: {booking.PickupLocation}; " +
            $"Trả: {booking.ReturnLocation}; phí giao nhận: {booking.DeliveryFee:N0} đồng.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật địa điểm và tính lại phí giao nhận.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(
        int id,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == id, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        var rentalPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Rental &&
            payment.Status == PaymentStatus.Paid);

        if (booking.Status != BookingStatus.Paid || !rentalPaid)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đã thanh toán tiền thuê mới được ghi nhận xe đã chuẩn bị xong.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var entityId = id.ToString();
        var alreadyPrepared = await _dbContext.AuditLogs
            .AsNoTracking()
            .AnyAsync(log =>
                log.Action == "VehiclePrepared" &&
                log.EntityName == nameof(Booking) &&
                log.EntityId == entityId,
                cancellationToken);

        if (!alreadyPrepared)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = booking.CustomerId,
                Title = "Xe của bạn đã được chuẩn bị xong",
                Message =
                    $"SmartCar đã hoàn tất kiểm tra và chuẩn bị xe cho đơn #{booking.BookingId}. " +
                    $"Chúng tôi sẽ liên hệ xác nhận trước khi giao tại {booking.PickupLocation} " +
                    $"lúc {booking.PickupDate:dd/MM/yyyy HH:mm}. " +
                    $"Khi nhận xe, khách cần cọc bảo đảm {RentalPolicyConstants.SecurityDepositAmount:N0} đồng; " +
                    "cọc sẽ được quyết toán sau khi trả xe."
            });

            await _dbContext.SaveChangesAsync(cancellationToken);

            await WriteAuditAsync(
                "VehiclePrepared",
                id,
                $"Xe của đơn #{id} đã được kiểm tra và chuẩn bị xong; tiếp theo Admin liên hệ khách xác nhận trước khi xuất phát giao xe.",
                cancellationToken);
        }

        TempData["SuccessMessage"] = alreadyPrepared
            ? "Xe đã được ghi nhận chuẩn bị xong. Tiếp tục xác nhận lịch nhận với khách."
            : "Đã ghi nhận xe chuẩn bị xong và thông báo cho khách. Tiếp tục gọi/nhắn khách xác nhận trước khi xuất phát.";

        return RedirectToAction(
            "PreparePickup",
            "AdminBookingOperations",
            new { bookingId = id });
    }

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
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        return _auditService.WriteAsync(
            adminId,
            action,
            nameof(Booking),
            bookingId.ToString(),
            description,
            ipAddress: ipAddress,
            cancellationToken: cancellationToken);
    }
}
