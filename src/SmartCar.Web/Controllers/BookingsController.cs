using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class BookingsController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IDeliveryQuoteService _deliveryQuoteService;
    private readonly IDocumentService _documentService;
    private readonly IUserBankAccountService _bankAccountService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ApplicationDbContext _dbContext;

    public BookingsController(
        IBookingService bookingService,
        IDeliveryQuoteService deliveryQuoteService,
        IDocumentService documentService,
        IUserBankAccountService bankAccountService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ApplicationDbContext dbContext)
    {
        _bookingService = bookingService;
        _deliveryQuoteService = deliveryQuoteService;
        _documentService = documentService;
        _bankAccountService = bankAccountService;
        _userManager = userManager;
        _configuration = configuration;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> SearchLocations(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return Json(new
            {
                succeeded = true,
                results = Array.Empty<object>()
            });
        }

        try
        {
            var locations = await _deliveryQuoteService.SearchLocationsAsync(
                query,
                6,
                cancellationToken);

            return Json(new
            {
                succeeded = true,
                results = locations.Select(item => new
                {
                    displayName = item.DisplayName,
                    latitude = item.Latitude,
                    longitude = item.Longitude
                })
            });
        }
        catch
        {
            return Json(new
            {
                succeeded = false,
                error = "Chưa thể tìm địa điểm lúc này. Vui lòng thử lại."
            });
        }
    }

    [HttpGet]
    public async Task<IActionResult> DeliveryQuote(
        string pickupMethod,
        string pickupLocation,
        string returnLocation,
        CancellationToken cancellationToken)
    {
        var quote = await _deliveryQuoteService.CalculateAsync(
            pickupMethod,
            pickupLocation,
            returnLocation,
            cancellationToken);

        if (!quote.Succeeded)
        {
            return Json(new
            {
                succeeded = false,
                error = quote.Error
            });
        }

        return Json(new
        {
            succeeded = true,
            pickupLocation = quote.PickupLocation,
            returnLocation = quote.ReturnLocation,
            pickupDeliveryDistanceKm = quote.PickupDeliveryDistanceKm,
            returnCollectionDistanceKm = quote.ReturnCollectionDistanceKm,
            totalDistanceKm = quote.TotalDistanceKm,
            deliveryRatePerKm = quote.DeliveryRatePerKm,
            deliveryFee = quote.DeliveryFee,
            smartCarLocation = DeliveryConstants.SmartCarLocation
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        CreateBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var customer = await _userManager.FindByIdAsync(customerId);
        if (customer is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = string.Join(
                "; ",
                ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage));

            return RedirectToAction("Details", "Vehicles", new
            {
                id = model.VehicleId,
                pickupDate = model.PickupDate,
                returnDate = model.ReturnDate
            });
        }

        if (string.IsNullOrWhiteSpace(customer.PhoneNumber))
        {
            TempData["WarningMessage"] =
                "Vui lòng bổ sung số điện thoại liên hệ trước khi gửi yêu cầu thuê xe để SmartCar có thể xác nhận và hỗ trợ đơn của bạn.";

            return RedirectToAction(
                "Index",
                "Profile",
                new
                {
                    tab = "profile",
                    returnVehicleId = model.VehicleId,
                    pickupDate = model.PickupDate,
                    returnDate = model.ReturnDate
                });
        }

        var hasValidRentalDocuments = await _documentService.HasValidRentalDocumentsAsync(
            customerId,
            model.ReturnDate,
            cancellationToken);

        if (!hasValidRentalDocuments)
        {
            TempData["WarningMessage"] =
                "Vui lòng hoàn tất xác minh CCCD và GPLX còn hiệu lực đến ngày trả xe trước khi gửi yêu cầu thuê xe.";

            return RedirectToAction(
                "Index",
                "Profile",
                new
                {
                    tab = "documents",
                    returnVehicleId = model.VehicleId,
                    pickupDate = model.PickupDate,
                    returnDate = model.ReturnDate
                });
        }

        var bankAccount = await _bankAccountService.GetDefaultAsync(
            customerId,
            cancellationToken);

        if (bankAccount is null)
        {
            TempData["WarningMessage"] =
                "Vui lòng thêm tài khoản ngân hàng trước khi gửi yêu cầu thuê xe. SmartCar dùng tài khoản này để hoàn tiền, hoàn cọc hoặc chuyển trả các khoản phát sinh khi cần.";

            return RedirectToAction(
                "Index",
                "Profile",
                new
                {
                    tab = "banking",
                    returnVehicleId = model.VehicleId,
                    pickupDate = model.PickupDate,
                    returnDate = model.ReturnDate
                });
        }

        var result = await _bookingService.CreateAsync(
            customerId,
            new CreateBookingRequest(
                model.VehicleId,
                model.PickupDate,
                model.ReturnDate,
                model.PickupMethod,
                model.PickupLocation,
                model.ReturnLocation),
            cancellationToken);

        if (!result.Succeeded || !result.BookingId.HasValue)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToAction("Details", "Vehicles", new
            {
                id = model.VehicleId,
                pickupDate = model.PickupDate,
                returnDate = model.ReturnDate
            });
        }

        TempData["SuccessMessage"] =
            $"Đã gửi yêu cầu thuê xe. SmartCar sẽ liên hệ qua số {customer.PhoneNumber}. Nếu cần hỗ trợ, bạn có thể gọi Hotline {GetSupportPhone()}.";
        return RedirectToAction(nameof(Details), new { id = result.BookingId.Value });
    }

    [HttpGet]
    public async Task<IActionResult> MyBookings(CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        return View(await _bookingService.GetCustomerBookingsAsync(
            customerId,
            cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEarlyReturn(
        int bookingId,
        DateTime requestedReturnAt,
        string? returnLocation,
        string reason,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang thuê và đã bàn giao xe mới có thể gửi yêu cầu trả xe sớm.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var normalizedReason = reason?.Trim() ?? string.Empty;
        var normalizedLocation = string.IsNullOrWhiteSpace(returnLocation)
            ? booking.ReturnLocation?.Trim() ?? string.Empty
            : returnLocation.Trim();

        if (requestedReturnAt <= DateTime.Now)
        {
            TempData["ErrorMessage"] = "Thời gian muốn trả xe phải ở sau thời điểm hiện tại.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        if (requestedReturnAt >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Thời gian yêu cầu phải sớm hơn thời gian trả xe đã thỏa thuận. Nếu trả đúng hoặc muộn hơn, không cần dùng chức năng trả sớm.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        if (string.IsNullOrWhiteSpace(normalizedLocation) || normalizedLocation.Length > 250)
        {
            TempData["ErrorMessage"] = "Địa điểm trả xe phải có nội dung và không vượt quá 250 ký tự.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        if (normalizedReason.Length < 5 || normalizedReason.Length > 500)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do trả sớm từ 5 đến 500 ký tự.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var bookingIdText = bookingId.ToString();
        var latestEarlyReturnAction = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingIdText &&
                RentalLifecycleAuditHelper.EarlyReturnActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => log.Action)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestEarlyReturnAction is RentalLifecycleAuditHelper.EarlyReturnRequested or
            RentalLifecycleAuditHelper.EarlyReturnApproved)
        {
            TempData["ErrorMessage"] =
                latestEarlyReturnAction == RentalLifecycleAuditHelper.EarlyReturnRequested
                    ? "Đơn đang có yêu cầu trả xe sớm chờ SmartCar xử lý."
                    : "Yêu cầu trả xe sớm đã được SmartCar chấp thuận. Hãy thực hiện theo thời gian và địa điểm đã thống nhất.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var createdAt = DateTime.UtcNow;
        var newValues = RentalLifecycleAuditHelper.SerializeEarlyReturn(
            requestedReturnAt,
            normalizedLocation,
            normalizedReason);

        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = customerId,
            Action = RentalLifecycleAuditHelper.EarlyReturnRequested,
            EntityName = nameof(Booking),
            EntityId = bookingIdText,
            Description =
                $"Khách yêu cầu trả sớm đơn #{bookingId} vào {requestedReturnAt:dd/MM/yyyy HH:mm} tại {normalizedLocation}. " +
                $"Lý do: {normalizedReason}. Booking vẫn giữ trạng thái Rented cho đến khi xe thực tế được nhận lại.",
            NewValues = newValues,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = createdAt
        });

        _dbContext.Notifications.Add(new Notification
        {
            UserId = customerId,
            Title = "Đã gửi yêu cầu trả xe sớm",
            Message =
                $"SmartCar đã nhận yêu cầu trả xe sớm của đơn #{bookingId}: {requestedReturnAt:dd/MM/yyyy HH:mm} tại {normalizedLocation}. " +
                "Xe vẫn thuộc trách nhiệm của bạn và đơn vẫn ở trạng thái Đang thuê cho đến khi SmartCar thực tế nhận lại xe và lập biên bản trả xe."
        });

        await AddAdminNotificationsAsync(
            $"Yêu cầu trả xe sớm|Booking:{bookingId}",
            $"Khách yêu cầu trả sớm đơn #{bookingId} vào {requestedReturnAt:dd/MM/yyyy HH:mm} tại {normalizedLocation}. Vào chi tiết đơn để duyệt hoặc từ chối.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] =
            "Đã gửi yêu cầu trả xe sớm. SmartCar sẽ xác nhận lại thời gian và địa điểm. Trong thời gian chờ, xe vẫn thuộc trách nhiệm của bạn.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking = await _bookingService.GetCustomerBookingAsync(
            id,
            customerId,
            cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        var bookingIdText = id.ToString();
        var vehiclePreparedAt = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.Action == "VehiclePrepared" &&
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingIdText)
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => (DateTime?)log.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var latestEarlyReturnLog = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingIdText &&
                RentalLifecycleAuditHelper.EarlyReturnActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => new { log.Action, log.NewValues, log.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var latestTerminationLog = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(Booking) &&
                log.EntityId == bookingIdText &&
                RentalLifecycleAuditHelper.RentalTerminationActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => new { log.Action, log.NewValues, log.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        var vehiclePreparedLocal = vehiclePreparedAt?.ToLocalTime();
        ViewBag.VehiclePreparedAt = vehiclePreparedLocal;
        ViewBag.EarlyReturn = latestEarlyReturnLog is null
            ? null
            : RentalLifecycleAuditHelper.ParseEarlyReturn(
                latestEarlyReturnLog.Action,
                latestEarlyReturnLog.NewValues,
                latestEarlyReturnLog.CreatedAt.ToLocalTime());
        ViewBag.RentalTermination = latestTerminationLog is null
            ? null
            : RentalLifecycleAuditHelper.ParseTermination(
                latestTerminationLog.Action,
                latestTerminationLog.NewValues,
                latestTerminationLog.CreatedAt.ToLocalTime());

        if (booking.Status == BookingStatus.Paid &&
            vehiclePreparedLocal.HasValue &&
            TempData["SuccessMessage"] is null)
        {
            var pickupLocation = string.IsNullOrWhiteSpace(booking.PickupLocation)
                ? "địa điểm đã thỏa thuận"
                : booking.PickupLocation;

            TempData["SuccessMessage"] =
                $"Xe của đơn #{booking.BookingId} đã được SmartCar kiểm tra và xác nhận chuẩn bị xong lúc {vehiclePreparedLocal.Value:dd/MM/yyyy HH:mm}. " +
                $"Xe đã được dành cho lịch nhận {booking.PickupDate:dd/MM/yyyy HH:mm} tại {pickupLocation}. " +
                "Bạn chỉ cần chờ đến thời gian nhận xe; SmartCar sẽ liên hệ lại gần giờ nhận để xác nhận trước khi bàn giao. " +
                $"Cọc bảo đảm {RentalPolicyConstants.SecurityDepositAmount:N0} đồng đã được thanh toán cùng tiền thuê, bạn không cần thanh toán lại khi nhận xe.";
        }

        ViewBag.SmartCarSupportPhone = GetSupportPhone();

        ViewBag.PaymentBankName =
            _configuration["PaymentQr:BankName"] ?? "MB Bank";

        ViewBag.PaymentAccountNumber =
            _configuration["PaymentQr:AccountNumber"] ?? "0123456789";

        ViewBag.PaymentAccountHolder =
            _configuration["PaymentQr:AccountHolder"] ?? "SMARTCAR";

        ViewBag.PaymentQrImagePath =
            _configuration["PaymentQr:QrImagePath"]
            ?? "/images/payment/bank-qr.png";

        return View("DetailsRentalLifecycle", booking);
    }

    private async Task AddAdminNotificationsAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var adminRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.RoleId == adminRoleId)
            .Select(userRole => userRole.UserId)
            .Distinct()
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

    private string GetSupportPhone() =>
        _configuration["SmartCar:SupportPhone"]?.Trim() is { Length: > 0 } phone
            ? phone
            : "0982223792";
}
