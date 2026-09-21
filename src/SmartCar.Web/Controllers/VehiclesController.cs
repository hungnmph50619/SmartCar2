using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using SmartCar.Application.Features.Brands;
using SmartCar.Application.Features.Documents;
using SmartCar.Application.Features.Reviews;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Infrastructure.Services;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[AllowAnonymous]
public sealed class VehiclesController : Controller
{
    private readonly IVehicleService _vehicleService;
    private readonly IBrandService _brandService;
    private readonly IReviewService _reviewService;
    private readonly IDocumentService _documentService;
    private readonly IUserBankAccountService _bankAccountService;
    private readonly IConfiguration _configuration;
    private readonly ApplicationDbContext _dbContext;

    public VehiclesController(
        IVehicleService vehicleService,
        IBrandService brandService,
        IReviewService reviewService,
        IDocumentService documentService,
        IUserBankAccountService bankAccountService,
        IConfiguration configuration,
        ApplicationDbContext dbContext)
    {
        _vehicleService = vehicleService;
        _brandService = brandService;
        _reviewService = reviewService;
        _documentService = documentService;
        _bankAccountService = bankAccountService;
        _configuration = configuration;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] VehicleSearchViewModel model,
        CancellationToken cancellationToken)
    {
        model.SortBy = string.IsNullOrWhiteSpace(model.SortBy) ? "price_asc" : model.SortBy;

        await LoadBrandsAsync(model.BrandId, cancellationToken);
        ViewBag.Search = model;

        var policy = await BusinessPolicyStore.ReadAsync(_dbContext, cancellationToken);
        ViewBag.VehicleTurnaroundMinutes = policy.VehicleTurnaroundMinutes;
        ViewBag.MinimumPickupLeadMinutes = policy.MinimumPickupLeadMinutes;
        var earliestPickup = DateTime.Now.AddMinutes(policy.MinimumPickupLeadMinutes);
        if (model.PickupDate < earliestPickup)
        {
            ModelState.AddModelError(
                nameof(model.PickupDate),
                $"Ngày giờ nhận xe phải cách hiện tại ít nhất {policy.MinimumPickupLeadMinutes} phút.");
        }

        if (model.ReturnDate <= model.PickupDate)
        {
            ModelState.AddModelError(nameof(model.ReturnDate), "Ngày giờ trả xe phải sau ngày giờ nhận xe.");
        }

        if (model.MinDailyPrice.HasValue && model.MaxDailyPrice.HasValue &&
            model.MinDailyPrice.Value > model.MaxDailyPrice.Value)
        {
            ModelState.AddModelError(nameof(model.MaxDailyPrice), "Giá tối đa phải lớn hơn hoặc bằng giá tối thiểu.");
        }

        if (model.MinManufactureYear.HasValue && model.MinManufactureYear.Value > DateTime.Now.Year)
        {
            ModelState.AddModelError(nameof(model.MinManufactureYear), "Năm sản xuất tối thiểu không được lớn hơn năm hiện tại.");
        }

        if (!ModelState.IsValid)
        {
            return View(Array.Empty<VehicleDto>());
        }

        var vehicles = await _vehicleService.SearchAvailableAsync(
            new VehicleSearchRequest(
                model.PickupDate,
                model.ReturnDate,
                model.BrandId,
                model.Seats,
                model.Transmission,
                model.FuelType,
                model.MinDailyPrice,
                model.MaxDailyPrice,
                model.MinManufactureYear,
                PickupMethod: VehiclePickupMethod.StorePickup),
            cancellationToken);

        IEnumerable<VehicleDto> sortedVehicles = model.SortBy switch
        {
            "price_desc" => vehicles
                .OrderByDescending(vehicle => vehicle.DailyPrice)
                .ThenByDescending(vehicle => vehicle.ManufactureYear),
            "year_desc" => vehicles
                .OrderByDescending(vehicle => vehicle.ManufactureYear)
                .ThenBy(vehicle => vehicle.DailyPrice),
            _ => vehicles
                .OrderBy(vehicle => vehicle.DailyPrice)
                .ThenByDescending(vehicle => vehicle.ManufactureYear)
        };

        return View(sortedVehicles.ToArray());
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        int id,
        DateTime? pickupDate,
        DateTime? returnDate,
        CancellationToken cancellationToken)
    {
        var vehicle = await _vehicleService.GetByIdAsync(id, cancellationToken);
        if (vehicle is null || vehicle.Status == VehicleStatus.Inactive)
        {
            return NotFound();
        }

        var selectedPickupDate = pickupDate ?? DateTime.Now.AddDays(1);
        var selectedReturnDate = returnDate ?? DateTime.Now.AddDays(2);

        var reviews = await _reviewService.GetVehicleReviewsAsync(id, cancellationToken);
        ViewBag.Reviews = reviews;
        ViewBag.AverageRating = reviews.Count == 0 ? 0 : reviews.Average(review => review.Rating);
        ViewBag.PickupDate = selectedPickupDate;
        ViewBag.ReturnDate = selectedReturnDate;

        // Thông tin cửa hàng dùng cho lựa chọn nhận xe trực tiếp.
        ViewBag.StoreAddress =
            _configuration["SmartCar:StoreAddress"]
            ?? "SmartCar - Tòa FPT Polytechnic, Nam Từ Liêm, Hà Nội";

        // Kiểm tra lại đúng khoảng thời gian ngay tại trang chi tiết để tránh
        // khách mở URL cũ rồi gửi đơn khi xe đã phát sinh lịch thuê khác.
        var periodVehicles = await _vehicleService.SearchAvailableAsync(
            new VehicleSearchRequest(
                selectedPickupDate,
                selectedReturnDate),
            cancellationToken);

        ViewBag.AvailableForSelectedPeriod =
            periodVehicles.Any(item => item.VehicleId == id);

        ViewBag.KycVerified = false;
        ViewBag.KycVerifiedCount = 0;
        ViewBag.KycTotal = 2;
        ViewBag.CitizenApproved = false;
        ViewBag.LicenseApproved = false;
        ViewBag.CitizenValidForRental = false;
        ViewBag.LicenseValidForRental = false;
        ViewBag.CitizenExpiryDate = null;
        ViewBag.LicenseExpiryDate = null;
        ViewBag.HasBankAccount = false;

        if (User.Identity?.IsAuthenticated == true && User.IsInRole(RoleNames.Customer))
        {
            var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(customerId))
            {
                var documents = await _documentService.GetCustomerDocumentsAsync(customerId, cancellationToken);
                var citizenFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
                var citizenBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenIdBack);
                var licenseFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);
                var licenseBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicenseBack);

                var citizenApproved = citizenFront is not null &&
                                      citizenBack is not null &&
                                      citizenFront.Status == DocumentStatus.Verified &&
                                      citizenBack.Status == DocumentStatus.Verified &&
                                      citizenFront.HasRequiredData &&
                                      citizenBack.HasRequiredData;

                var licenseApproved = licenseFront is not null &&
                                      licenseBack is not null &&
                                      licenseFront.Status == DocumentStatus.Verified &&
                                      licenseBack.Status == DocumentStatus.Verified &&
                                      licenseFront.HasRequiredData &&
                                      licenseBack.HasRequiredData;

                var citizenValidForRental = citizenApproved &&
                                            citizenFront!.ExpiryDate.HasValue &&
                                            citizenFront.ExpiryDate.Value.Date >= selectedReturnDate.Date;

                var licenseValidForRental = licenseApproved &&
                                            licenseFront!.ExpiryDate.HasValue &&
                                            licenseFront.ExpiryDate.Value.Date >= selectedReturnDate.Date;

                var approvedCount = (citizenApproved ? 1 : 0) + (licenseApproved ? 1 : 0);
                ViewBag.KycVerifiedCount = approvedCount;
                ViewBag.KycVerified = citizenValidForRental && licenseValidForRental;
                ViewBag.CitizenApproved = citizenApproved;
                ViewBag.LicenseApproved = licenseApproved;
                ViewBag.CitizenValidForRental = citizenValidForRental;
                ViewBag.LicenseValidForRental = licenseValidForRental;
                ViewBag.CitizenExpiryDate = citizenFront?.ExpiryDate;
                ViewBag.LicenseExpiryDate = licenseFront?.ExpiryDate;
                ViewBag.HasBankAccount = await _bankAccountService.GetDefaultAsync(
                    customerId,
                    cancellationToken) is not null;
            }
        }

        return View("DetailsV2", vehicle);
    }

    private async Task LoadBrandsAsync(int? selectedId, CancellationToken cancellationToken)
    {
        ViewBag.Brands = new SelectList(
            await _brandService.GetActiveAsync(cancellationToken),
            "BrandId",
            "BrandName",
            selectedId);
    }
}
