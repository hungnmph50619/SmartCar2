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
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[AllowAnonymous]
public sealed class VehiclesController : Controller
{
    private readonly IVehicleService _vehicleService;
    private readonly IBrandService _brandService;
    private readonly IReviewService _reviewService;
    private readonly IDocumentService _documentService;

    public VehiclesController(
        IVehicleService vehicleService,
        IBrandService brandService,
        IReviewService reviewService,
        IDocumentService documentService)
    {
        _vehicleService = vehicleService;
        _brandService = brandService;
        _reviewService = reviewService;
        _documentService = documentService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] VehicleSearchViewModel model,
        CancellationToken cancellationToken)
    {
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

        if (model.Seats is <= 0)
        {
            ModelState.AddModelError(nameof(model.Seats), "Số chỗ tối thiểu phải lớn hơn 0.");
        }

        if (model.MaxDailyPrice is <= 0)
        {
            ModelState.AddModelError(nameof(model.MaxDailyPrice), "Giá thuê tối đa phải lớn hơn 0.");
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

        return View(vehicles);
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
