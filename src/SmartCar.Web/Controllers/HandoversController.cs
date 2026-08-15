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
    private const int MinimumImages = 6;
    private const int MaximumImages = 15;
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
            return RedirectToAction(nameof(PendingCustomerSignature), new { bookingId });
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
                $"Chưa xác nhận cọc bảo đảm {RentalPolicyConstants.SecurityDepositAmount:N0} đồng trong giao dịch thanh toán ban đầu. Không thể lập biên bản bàn giao.";
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
            HandoverAt = DateTime.Now > booking.PickupDate
                ? DateTime.Now
                : booking.PickupDate,
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
                    "Đơn không còn ở trạng thái Sẵn sàng giao xe. Hãy quay lại chi tiết đơn để kiểm tra trạng thái hiện tại.");
            }

            if (booking.HasHandover)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Biên bản bàn giao đã được lập và đang chờ khách ký xác nhận.");
            }

            if (!booking.Payments.Any(payment =>
                    payment.Type == PaymentType.Deposit && payment.Status == PaymentStatus.Paid))
            {
                ModelState.AddModelError(
                    string.Empty,
                    $"Cọc bảo đảm {RentalPolicyConstants.SecurityDepositAmount:N0} đồng chưa được xác nhận thanh toán. Không thể lập biên bản giao xe.");
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

        await ValidateImagesAsync(model.Images, cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var imagePaths = await SaveImagesAsync(
            model.BookingId,
            model.Images,
            cancellationToken);

        const string conditionReference =
            "Tình trạng xe được ghi nhận và đối chiếu theo bộ ảnh bàn giao điện tử.";

        var result = await _handoverService.CreateAsync(
            new CreateHandoverRequest(
                model.BookingId,
                model.HandoverAt,
                model.Mileage,
                model.FuelLevel,
                conditionReference,
                conditionReference,
                model.Accessories,
                string.Join(';', imagePaths),
                model.Notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedImages(imagePaths);
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            if (vehicleContext is not null)
            {
                SetVehicleContext(vehicleContext);
            }

            return View(model);
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "CreateHandoverDraft",
            nameof(VehicleHandover),
            model.BookingId.ToString(),
            $"Lập biên bản bàn giao điện tử cho đơn #{model.BookingId}; đã đối chiếu đúng người nhận và CCCD/GPLX bản gốc; " +
            $"ODO {model.Mileage:N0} km, nhiên liệu/pin {model.FuelLevel}, {imagePaths.Count} ảnh bàn giao. " +
            "Biên bản đang chờ chính khách thuê xem và ký xác nhận; Booking chưa chuyển sang Rented.",
            newValues:
                $"Images={imagePaths.Count};Mileage={model.Mileage};FuelLevel={model.FuelLevel};AwaitingCustomerSignature=true",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã tạo biên bản bàn giao và gửi khách xác nhận. Chưa giao chìa khóa; đơn chỉ chuyển sang Đang thuê sau khi khách ký biên bản.";
        return RedirectToAction(nameof(PendingCustomerSignature), new { bookingId = model.BookingId });
    }

    [HttpGet]
    public async Task<IActionResult> PendingCustomerSignature(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup || !booking.HasHandover)
        {
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        return View(booking);
    }

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
    }

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images.Where(file => file.Length > 0).ToList();
        if (selectedImages.Count < MinimumImages)
        {
            ModelState.AddModelError(nameof(HandoverViewModel.Images),
                $"Vui lòng tải tối thiểu {MinimumImages} ảnh đối chiếu khi bàn giao: trước xe, sau xe, hai bên thân xe, đồng hồ km/nhiên liệu và nội thất.");
            return;
        }

        if (selectedImages.Count > MaximumImages)
        {
            ModelState.AddModelError(nameof(HandoverViewModel.Images),
                $"Chỉ được tải tối đa {MaximumImages} ảnh bàn giao.");
        }

        foreach (var image in selectedImages)
        {
            var error = await ImageFileValidator.ValidateAsync(
                image,
                MaximumImageBytes,
                cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(HandoverViewModel.Images),
                    $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/handovers/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var paths = new List<string>();
        foreach (var image in images.Where(file => file.Length > 0))
        {
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"{Guid.NewGuid():N}{extension}";
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
