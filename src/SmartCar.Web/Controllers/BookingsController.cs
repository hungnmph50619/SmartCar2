using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;
using System.Security.Claims;
using System.Globalization;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class BookingsController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IDocumentService _documentService;
    private readonly IUserBankAccountService _bankAccountService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly ApplicationDbContext _dbContext;

    public BookingsController(
        IBookingService bookingService,
        IDocumentService documentService,
        IUserBankAccountService bankAccountService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ApplicationDbContext dbContext)
    {
        _bookingService = bookingService;
        _documentService = documentService;
        _bankAccountService = bankAccountService;
        _userManager = userManager;
        _configuration = configuration;
        _dbContext = dbContext;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        CreateBookingViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var customer =
            await _userManager.FindByIdAsync(customerId);

        if (customer is null)
        {
            return Challenge();
        }
        // ============================================================
        // TỌA ĐỘ GIAO XE
        // ============================================================
        // JavaScript gửi tọa độ theo chuẩn dấu chấm, ví dụ:
        // 21.0381298 / 105.7424982.
        // Không bind trực tiếp vào decimal vì trên máy chạy vi-VN
        // dấu chấm có thể bị hiểu sai và biến tọa độ thành số rất lớn.

        decimal? deliveryLatitude = null;
        decimal? deliveryLongitude = null;

        if (model.PickupMethod ==
            VehiclePickupMethod.Delivery)
        {
            if (string.IsNullOrWhiteSpace(
                    model.DeliveryAddress))
            {
                TempData["ErrorMessage"] =
                    "Vui lòng nhập địa chỉ giao xe.";

                return RedirectToAction(
                    "Details",
                    "Vehicles",
                    new
                    {
                        id = model.VehicleId,
                        pickupDate = model.PickupDate,
                        returnDate = model.ReturnDate
                    });
            }

            if (string.IsNullOrWhiteSpace(
                    model.DeliveryLatitude) ||
                string.IsNullOrWhiteSpace(
                    model.DeliveryLongitude))
            {
                TempData["ErrorMessage"] =
                    "Vui lòng chọn chính xác vị trí giao xe trên bản đồ.";

                return RedirectToAction(
                    "Details",
                    "Vehicles",
                    new
                    {
                        id = model.VehicleId,
                        pickupDate = model.PickupDate,
                        returnDate = model.ReturnDate
                    });
            }

            // Hỗ trợ cả dữ liệu cũ dùng dấu phẩy và dữ liệu mới dùng dấu chấm.
            var latitudeText =
                model.DeliveryLatitude
                    .Trim()
                    .Replace(',', '.');

            var longitudeText =
                model.DeliveryLongitude
                    .Trim()
                    .Replace(',', '.');

            var latitudeOk =
                decimal.TryParse(
                    latitudeText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedLatitude);

            var longitudeOk =
                decimal.TryParse(
                    longitudeText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedLongitude);

            if (!latitudeOk ||
                !longitudeOk)
            {
                TempData["ErrorMessage"] =
                    "Không đọc được tọa độ giao xe. " +
                    "Vui lòng chọn lại vị trí trên bản đồ.";

                return RedirectToAction(
                    "Details",
                    "Vehicles",
                    new
                    {
                        id = model.VehicleId,
                        pickupDate = model.PickupDate,
                        returnDate = model.ReturnDate
                    });
            }

            if (parsedLatitude is < -90 or > 90 ||
                parsedLongitude is < -180 or > 180)
            {
                TempData["ErrorMessage"] =
                    $"Vị trí giao xe trên bản đồ không hợp lệ " +
                    $"({parsedLatitude.ToString(CultureInfo.InvariantCulture)}, " +
                    $"{parsedLongitude.ToString(CultureInfo.InvariantCulture)}). " +
                    "Vui lòng chọn lại vị trí.";

                return RedirectToAction(
                    "Details",
                    "Vehicles",
                    new
                    {
                        id = model.VehicleId,
                        pickupDate = model.PickupDate,
                        returnDate = model.ReturnDate
                    });
            }

            deliveryLatitude = parsedLatitude;
            deliveryLongitude = parsedLongitude;
        }

        // ============================================================
        // SỐ ĐIỆN THOẠI
        // ============================================================

        if (string.IsNullOrWhiteSpace(customer.PhoneNumber))
        {
            TempData["WarningMessage"] =
                "Vui lòng bổ sung số điện thoại liên hệ trước khi gửi yêu cầu thuê xe " +
                "để SmartCar có thể xác nhận và hỗ trợ đơn của bạn.";

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

        // ============================================================
        // GIẤY TỜ THUÊ XE
        // ============================================================

        var hasValidRentalDocuments =
            await _documentService.HasValidRentalDocumentsAsync(
                customerId,
                model.ReturnDate,
                cancellationToken);

        if (!hasValidRentalDocuments)
        {
            TempData["WarningMessage"] =
                "Vui lòng hoàn tất xác minh CCCD và GPLX còn hiệu lực đến ngày trả xe " +
                "trước khi gửi yêu cầu thuê xe.";

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

        // ============================================================
        // TÀI KHOẢN NGÂN HÀNG
        // ============================================================

        var bankAccount =
            await _bankAccountService.GetDefaultAsync(
                customerId,
                cancellationToken);

        if (bankAccount is null)
        {
            TempData["WarningMessage"] =
                "Vui lòng thêm tài khoản ngân hàng trước khi gửi yêu cầu thuê xe. " +
                "SmartCar dùng tài khoản này để hoàn tiền, hoàn cọc hoặc chuyển trả " +
                "các khoản phát sinh khi cần.";

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

        // ============================================================
        // TẠO ĐƠN
        // ============================================================

        var request =
            new CreateBookingRequest(
                model.VehicleId,
                model.PickupDate,
                model.ReturnDate,
                model.PickupMethod,
                model.DeliveryAddress,
                deliveryLatitude,
                deliveryLongitude,
                model.PolicyVersion);

        var result =
            await _bookingService.CreateAsync(
                customerId,
                request,
                cancellationToken);

        if (!result.Succeeded ||
            !result.BookingId.HasValue)
        {
            TempData["ErrorMessage"] =
                string.Join("; ", result.Errors);

            return RedirectToAction(
                "Details",
                "Vehicles",
                new
                {
                    id = model.VehicleId,
                    pickupDate = model.PickupDate,
                    returnDate = model.ReturnDate
                });
        }

        TempData["SuccessMessage"] =
            $"Đã gửi yêu cầu thuê xe. SmartCar sẽ liên hệ qua số " +
            $"{customer.PhoneNumber}. Nếu cần hỗ trợ, bạn có thể gọi Hotline " +
            $"{GetSupportPhone()}.";

        return RedirectToAction(
            nameof(Details),
            new
            {
                id = result.BookingId.Value
            });
    }

    [HttpGet]
    public async Task<IActionResult> MyBookings(
        CancellationToken cancellationToken)
    {
        var customerId =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var bookings =
            await _bookingService.GetCustomerBookingsAsync(
                customerId,
                cancellationToken);

        return View(bookings);
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        int id,
        CancellationToken cancellationToken)
    {
        var customerId =
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking =
            await _bookingService.GetCustomerBookingAsync(
                id,
                customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        // Hồ sơ giao/trả được tải trực tiếp theo đơn đã được kiểm tra quyền sở hữu ở trên.
        // Bao gồm cả ảnh chứng cứ và ảnh chụp/scan bản giấy đã ký trong ImagePaths.
        ViewBag.CustomerHandoverRecord = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == id, cancellationToken);

        ViewBag.CustomerReturnRecord = await _dbContext.VehicleReturns
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == id, cancellationToken);

        // Read-only customer guidance, using the policy saved on this booking.
        var depositPolicy = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == id && item.CustomerId == customerId)
            .Select(item => new
            {
                item.DepositHoldDaysApplied,
                ReturnedAt = item.VehicleReturn == null ? (DateTime?)null : item.VehicleReturn.ReturnedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (depositPolicy?.ReturnedAt is DateTime returnedAt)
        {
            ViewBag.CustomerDepositHoldDays = DepositHoldPolicy.NormalizeDays(depositPolicy.DepositHoldDaysApplied);
            ViewBag.CustomerDepositEligibleAt = DepositHoldPolicy.CalculateEligibleAt(
                returnedAt, depositPolicy.DepositHoldDaysApplied);
        }

        ViewBag.SmartCarSupportPhone =
            GetSupportPhone();

        ViewBag.PaymentBankName =
            _configuration["PaymentQr:BankName"]
            ?? "MB Bank";

        ViewBag.PaymentAccountNumber =
            _configuration["PaymentQr:AccountNumber"]
            ?? "0123456789";

        ViewBag.PaymentAccountHolder =
            _configuration["PaymentQr:AccountHolder"]
            ?? "SMARTCAR";

        ViewBag.PaymentQrImagePath =
            _configuration["PaymentQr:QrImagePath"]
            ?? "/images/payment/bank-qr.png";

        return View(booking);
    }

    private string GetSupportPhone()
    {
        var phone =
            _configuration["SmartCar:SupportPhone"]?.Trim();

        return !string.IsNullOrWhiteSpace(phone)
            ? phone
            : "0982223792";
    }
}

