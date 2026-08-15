using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class HandoversController : Controller
{
    private const int MinimumVehicleImages = 6;
    private const int MaximumVehicleImages = 15;
    private const int MinimumDocumentImages = 1;
    private const int MaximumDocumentImages = 3;
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private readonly IHandoverService _handoverService;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;

    public HandoversController(
        IHandoverService handoverService,
        IBookingService bookingService,
        IAuditService auditService,
        IWebHostEnvironment environment)
    {
        _handoverService = handoverService;
        _bookingService = bookingService;
        _auditService = auditService;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> Create(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status == BookingStatus.ReadyForPickup && booking.HasHandover)
        {
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang sẵn sàng giao xe mới được bắt đầu quy trình bàn giao.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var depositPaid = booking.Payments.Any(payment =>
            payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid);
        if (!depositPaid)
        {
            TempData["ErrorMessage"] =
                $"Chưa xác nhận cọc bảo đảm {RentalPolicyConstants.SecurityDepositAmount:N0} đồng. Không thể bàn giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (DateTime.Now >= booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var vehicleContext = await _handoverService.GetVehicleContextAsync(
            bookingId,
            cancellationToken);
        if (vehicleContext is null)
        {
            return NotFound();
        }

        SetVehicleContext(vehicleContext);
        SetBookingContext(booking);

        return View(new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = DateTime.Now,
            Mileage = vehicleContext.CurrentMileage
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(model.BookingId, cancellationToken);
        if (booking is null)
        {
            ModelState.AddModelError(string.Empty, "Không tìm thấy đơn thuê.");
        }
        else
        {
            SetBookingContext(booking);

            if (booking.Status != BookingStatus.ReadyForPickup)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Đơn không còn ở trạng thái Sẵn sàng giao xe.");
            }

            if (booking.HasHandover)
            {
                ModelState.AddModelError(string.Empty, "Đơn đã có biên bản bàn giao xe.");
            }

            if (!booking.Payments.Any(payment =>
                    payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid))
            {
                ModelState.AddModelError(
                    string.Empty,
                    $"Cọc bảo đảm {RentalPolicyConstants.SecurityDepositAmount:N0} đồng chưa được xác nhận thanh toán.");
            }
        }

        ValidateHandoverCheckpoints(model);

        var vehicleContext = await _handoverService.GetVehicleContextAsync(
            model.BookingId,
            cancellationToken);

        if (vehicleContext is null)
        {
            ModelState.AddModelError(string.Empty, "Không tìm thấy thông tin xe của đơn thuê.");
        }
        else
        {
            SetVehicleContext(vehicleContext);
        }

        await ValidateImageCollectionAsync(
            model.Images,
            MinimumVehicleImages,
            MaximumVehicleImages,
            nameof(HandoverViewModel.Images),
            "ảnh hiện trạng xe",
            cancellationToken);

        await ValidateImageCollectionAsync(
            model.SignedDocumentImages,
            MinimumDocumentImages,
            MaximumDocumentImages,
            nameof(HandoverViewModel.SignedDocumentImages),
            "ảnh biên bản giấy đã có chữ ký khách",
            cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var vehicleImagePaths = await SaveImagesAsync(
            model.BookingId,
            model.Images,
            "uploads/handovers",
            "vehicle",
            cancellationToken);
        var documentImagePaths = await SaveImagesAsync(
            model.BookingId,
            model.SignedDocumentImages,
            "uploads/handover-documents",
            "document",
            cancellationToken);

        var allEvidencePaths = vehicleImagePaths.Concat(documentImagePaths).ToList();
        const string conditionReference =
            "Tình trạng xe được ghi nhận bằng bộ ảnh hiện trạng và biên bản bàn giao giấy có chữ ký của khách.";

        var result = await _handoverService.CreateAsync(
            new CreateHandoverRequest(
                model.BookingId,
                model.HandoverAt,
                model.Mileage,
                model.FuelLevel,
                conditionReference,
                conditionReference,
                string.Join(';', allEvidencePaths),
                model.Notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedImages(allEvidencePaths);
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "CompletePhysicalHandover",
            nameof(VehicleHandover),
            model.BookingId.ToString(),
            $"Hoàn tất bàn giao đơn #{model.BookingId}. Đã đối chiếu người nhận, CCCD và GPLX bản gốc; " +
            $"số km khi giao {model.Mileage:N0} km; nhiên liệu/pin {model.FuelLevel}; " +
            $"{vehicleImagePaths.Count} ảnh hiện trạng xe và {documentImagePaths.Count} ảnh biên bản giấy có chữ ký khách đã được lưu.",
            newValues:
                $"VehicleImages={vehicleImagePaths.Count};SignedDocumentImages={documentImagePaths.Count};Mileage={model.Mileage};FuelLevel={model.FuelLevel}",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã hoàn tất bàn giao. Ảnh hiện trạng xe và ảnh biên bản giấy có chữ ký khách đã được lưu; đơn chuyển sang Đang thuê.";
        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    // Giữ các route cũ để bookmark trước đây không gây lỗi sau khi bỏ bước khách ký/check-in trên app.
    [HttpGet]
    public IActionResult PendingCustomerSignature(int bookingId) =>
        RedirectToAction("Details", "AdminBookings", new { id = bookingId });

    [HttpGet]
    public IActionResult PendingCustomerCheckIn(int bookingId) =>
        RedirectToAction("Details", "AdminBookings", new { id = bookingId });

    private void SetVehicleContext(HandoverVehicleContextDto context)
    {
        ViewBag.CurrentMileage = context.CurrentMileage;
        ViewBag.VehicleFuelType = context.FuelType;
    }

    private void SetBookingContext(BookingDetailsDto booking)
    {
        ViewBag.HandoverCustomerName = booking.CustomerName;
        ViewBag.HandoverCustomerPhone = booking.CustomerPhone;
        ViewBag.HandoverVehicleName = booking.VehicleName;
        ViewBag.HandoverLicensePlate = booking.LicensePlate;
        ViewBag.HandoverPickupDate = booking.PickupDate;
        ViewBag.HandoverPickupLocation = booking.PickupLocation;
        ViewBag.HandoverReturnDate = booking.ReturnDate;
    }

    private void ValidateHandoverCheckpoints(HandoverViewModel model)
    {
        if (!model.RecipientConfirmed)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.RecipientConfirmed),
                "Phải xác nhận người đang nhận xe đúng là khách thuê của đơn.");
        }

        if (!model.CitizenIdOriginalChecked)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.CitizenIdOriginalChecked),
                "Phải đối chiếu CCCD bản gốc tại thời điểm giao xe.");
        }

        if (!model.DrivingLicenseOriginalChecked)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.DrivingLicenseOriginalChecked),
                "Phải đối chiếu GPLX bản gốc tại thời điểm giao xe.");
        }

        if (!model.FinalHandoverConfirmed)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.FinalHandoverConfirmed),
                "Phải xác nhận khách đã ký trực tiếp trên biên bản giấy và ảnh biên bản đã được chụp rõ trước khi hoàn tất bàn giao.");
        }
    }

    private async Task ValidateImageCollectionAsync(
        IReadOnlyCollection<IFormFile> images,
        int minimum,
        int maximum,
        string modelKey,
        string description,
        CancellationToken cancellationToken)
    {
        var selected = images.Where(file => file.Length > 0).ToList();
        if (selected.Count < minimum)
        {
            ModelState.AddModelError(modelKey,
                $"Vui lòng tải tối thiểu {minimum} {description}.");
            return;
        }

        if (selected.Count > maximum)
        {
            ModelState.AddModelError(modelKey,
                $"Chỉ được tải tối đa {maximum} {description}.");
        }

        foreach (var image in selected)
        {
            var error = await ImageFileValidator.ValidateAsync(
                image,
                MaximumImageBytes,
                cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(modelKey, $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        string rootFolder,
        string filePrefix,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"{rootFolder}/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var paths = new List<string>();
        var index = 0;
        foreach (var image in images.Where(file => file.Length > 0))
        {
            index++;
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"{filePrefix}-{index:00}-{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(folder, fileName);

            await using var stream = System.IO.File.Create(fullPath);
            await image.CopyToAsync(stream, cancellationToken);
            paths.Add($"/{relativeFolder}/{fileName}");
        }

        return paths;
    }

    private void DeleteSavedImages(IEnumerable<string> imagePaths)
    {
        foreach (var imagePath in imagePaths)
        {
            var fullPath = Path.Combine(
                _environment.WebRootPath,
                imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }
    }
}
