using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Documents;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.ViewModels;
using SmartCar.Application.Features.Vehicles;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class BookingsController : Controller
{
    private readonly IBookingService _bookingService;
    private readonly IDocumentService _documentService;
    private readonly IVehicleService _vehicleService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;

    public BookingsController(
        IBookingService bookingService,
        IDocumentService documentService,
        IVehicleService vehicleService)
    {
        _bookingService = bookingService;
        _documentService = documentService;
        _vehicleService = vehicleService;
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration)
    {
        _bookingService = bookingService;
        _documentService = documentService;
        _userManager = userManager;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> Confirm(
    int vehicleId,
    DateTime pickupDate,
    DateTime returnDate,
    CancellationToken cancellationToken)
    {
        if (!BookingDateRules.IsValidRange(
                pickupDate,
                returnDate)) //Chống việc user sửa URL,  ví dụ user nhập pickupdate = hôm qua -> thì không cho tiếp tục
        {
            TempData["ErrorMessage"] =
                "Thời gian thuê xe không hợp lệ.";

            return RedirectToAction(
                "Details",
                "Vehicles",
                new
                {
                    id = vehicleId,
                    pickupDate,
                    returnDate
                });
        }
        //kiểm tra lại tìm xe, nếu không kiểm tra thì người dùng không thể biết được xe có còn trống hay không
        var availableVehicles =
            await _vehicleService.SearchAvailableAsync(
                new VehicleSearchRequest(
                    pickupDate,
                    returnDate),
                cancellationToken);

        var vehicle = availableVehicles
            .FirstOrDefault(item =>
                item.VehicleId == vehicleId);

        if (vehicle is null)
        {
            TempData["ErrorMessage"] =
                "Xe không còn trống trong khoảng thời gian bạn chọn.";

            return RedirectToAction(
                "Index",
                "Vehicles",
                new
                {
                    PickupDate = pickupDate,
                    ReturnDate = returnDate
                });
        }

        var customerId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var hasValidDocuments =
            await _documentService
                .HasValidRentalDocumentsAsync(
                    customerId,
                    returnDate,
                    cancellationToken);

        if (!hasValidDocuments)
        {
            return RedirectToAction(
                "Index",
                "Profile",
                new
                {
                    tab = "documents",
                    returnVehicleId = vehicleId,
                    pickupDate,
                    returnDate
                });
        }

        var numberOfDays =
            BookingDateRules.CalculateNumberOfDays(
                pickupDate,
                returnDate);

        var model = new BookingConfirmViewModel
        {
            VehicleId = vehicle.VehicleId,
            VehicleName = vehicle.VehicleName,
            BrandName = vehicle.BrandName,
            LicensePlate = vehicle.LicensePlate,
            PrimaryImagePath = vehicle.PrimaryImagePath,

            PickupDate = pickupDate,
            ReturnDate = returnDate,

            NumberOfDays = numberOfDays,
            DailyPrice = vehicle.DailyPrice,

            TotalAmount =
                numberOfDays * vehicle.DailyPrice
        };

        return View(model);
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

        var result = await _bookingService.CreateAsync(
            customerId,
            new CreateBookingRequest(model.VehicleId, model.PickupDate, model.ReturnDate),
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

        ViewBag.SmartCarSupportPhone = GetSupportPhone();
        return View(booking);
    }

    private string GetSupportPhone() =>
        _configuration["SmartCar:SupportPhone"]?.Trim() is { Length: > 0 } phone
            ? phone
            : "0982223792";
}
