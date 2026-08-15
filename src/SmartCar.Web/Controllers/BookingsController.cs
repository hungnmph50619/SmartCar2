using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
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
                log.EntityName == "Booking" &&
                log.EntityId == bookingIdText)
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => (DateTime?)log.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        ViewBag.VehiclePreparedAt = vehiclePreparedAt?.ToLocalTime();
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

        return View(booking);
    }

    private string GetSupportPhone() =>
        _configuration["SmartCar:SupportPhone"]?.Trim() is { Length: > 0 } phone
            ? phone
            : "0982223792";
}
