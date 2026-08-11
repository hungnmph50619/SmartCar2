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

    public VehiclesController(
        IVehicleService vehicleService,
        IBrandService brandService,
        IReviewService reviewService,
        IDocumentService documentService,
        IUserBankAccountService bankAccountService)
    {
        _vehicleService = vehicleService;
        _brandService = brandService;
        _reviewService = reviewService;
        _documentService = documentService;
        _bankAccountService = bankAccountService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] VehicleSearchViewModel model,
        CancellationToken cancellationToken)
    {
        model.SortBy = string.IsNullOrWhiteSpace(model.SortBy) ? "price_asc" : model.SortBy;

        await LoadBrandsAsync(model.BrandId, cancellationToken);
        ViewBag.Search = model;

        var now = DateTime.Now;
        if (model.PickupDate <= now)
        {
            ModelState.AddModelError(nameof(model.PickupDate), "Ngày giờ nhận xe phải sau thời điểm hiện tại.");
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
                model.MaxDailyPrice),
            cancellationToken);

        IEnumerable<VehicleDto> filteredVehicles = vehicles;

        if (!string.IsNullOrWhiteSpace(model.FuelType))
        {
            filteredVehicles = filteredVehicles.Where(vehicle =>
                string.Equals(vehicle.FuelType, model.FuelType, StringComparison.OrdinalIgnoreCase));
        }

        if (model.MinDailyPrice.HasValue)
        {
            filteredVehicles = filteredVehicles.Where(vehicle =>
                vehicle.DailyPrice >= model.MinDailyPrice.Value);
        }

        if (model.MinManufactureYear.HasValue)
        {
            filteredVehicles = filteredVehicles.Where(vehicle =>
                vehicle.ManufactureYear >= model.MinManufactureYear.Value);
        }

        filteredVehicles = model.SortBy switch
        {
            "price_desc" => filteredVehicles
                .OrderByDescending(vehicle => vehicle.DailyPrice)
                .ThenByDescending(vehicle => vehicle.ManufactureYear),
            "year_desc" => filteredVehicles
                .OrderByDescending(vehicle => vehicle.ManufactureYear)
                .ThenBy(vehicle => vehicle.DailyPrice),
            _ => filteredVehicles
                .OrderBy(vehicle => vehicle.DailyPrice)
                .ThenByDescending(vehicle => vehicle.ManufactureYear)
        };

        return View(filteredVehicles.ToArray());
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
        ViewBag.KycVerified = false;
        ViewBag.KycVerifiedCount = 0;
        ViewBag.KycTotal = 2;
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

                var citizenVerified = citizenFront is not null &&
                                      citizenBack is not null &&
                                      citizenFront.Status == DocumentStatus.Verified &&
                                      citizenBack.Status == DocumentStatus.Verified &&
                                      citizenFront.HasRequiredData &&
                                      citizenBack.HasRequiredData &&
                                      citizenFront.ExpiryDate.HasValue &&
                                      citizenFront.ExpiryDate.Value.Date >= selectedReturnDate.Date;

                var licenseVerified = licenseFront is not null &&
                                      licenseBack is not null &&
                                      licenseFront.Status == DocumentStatus.Verified &&
                                      licenseBack.Status == DocumentStatus.Verified &&
                                      licenseFront.HasRequiredData &&
                                      licenseBack.HasRequiredData &&
                                      licenseFront.ExpiryDate.HasValue &&
                                      licenseFront.ExpiryDate.Value.Date >= selectedReturnDate.Date;

                var verifiedCount = (citizenVerified ? 1 : 0) + (licenseVerified ? 1 : 0);
                ViewBag.KycVerifiedCount = verifiedCount;
                ViewBag.KycVerified = verifiedCount == 2;
                ViewBag.HasBankAccount = await _bankAccountService.GetDefaultAsync(
                    customerId,
                    cancellationToken) is not null;
            }
        }

        return View(vehicle);
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
