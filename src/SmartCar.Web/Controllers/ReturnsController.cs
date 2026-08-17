using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Returns;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class ReturnsController : Controller
{
    private const int MaximumImages = 10;
    private const long MaximumImageBytes = 5 * 1024 * 1024;
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly IReturnService _returnService;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IWebHostEnvironment _environment;
    private readonly ApplicationDbContext _dbContext;

    public ReturnsController(
        IReturnService returnService,
        IBookingService bookingService,
        IAuditService auditService,
        IWebHostEnvironment environment,
        ApplicationDbContext dbContext)
    {
        _returnService = returnService;
        _bookingService = bookingService;
        _auditService = auditService;
        _environment = environment;
        _dbContext = dbContext;
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

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang thuê và đã bàn giao xe mới được lập biên bản trả xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var handover = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe để đối chiếu.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        SetHandoverBaseline(handover);

        return View(new ReturnViewModel
        {
            BookingId = bookingId,
            ReturnedAt = DateTime.Now > booking.ReturnDate ? DateTime.Now : booking.ReturnDate,
            Mileage = handover.Mileage
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ReturnViewModel model,
        CancellationToken cancellationToken)
    {
        await ValidateImagesAsync(model.Images, cancellationToken);
        if (!ModelState.IsValid)
        {
            await PopulateHandoverBaselineAsync(model.BookingId, cancellationToken);
            return View(model);
        }

        var imagePaths = await SaveImagesAsync(model.BookingId, model.Images, cancellationToken);

        var result = await _returnService.CreateAsync(
            new CreateReturnRequest(
                model.BookingId,
                model.ReturnedAt,
                model.Mileage,
                model.FuelLevel,
                null,
                null,
                model.HasDamage,
                string.Join(';', imagePaths),
                model.Notes),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedImages(imagePaths);
            AddErrors(result.Errors);
            await PopulateHandoverBaselineAsync(model.BookingId, cancellationToken);
            return View(model);
        }

        await WriteAuditAsync(
            "CreateReturn",
            nameof(VehicleReturn),
            model.BookingId,
            $"Lập biên bản trả xe cho đơn #{model.BookingId}, số km {model.Mileage}, {imagePaths.Count} ảnh, có hư hỏng mới: {(model.HasDamage ? "Có" : "Không")}.",
            cancellationToken);

        TempData["SuccessMessage"] =
            "Đã lập biên bản trả xe. In, ký và tải bản ký trước khi kết thúc kiểm tra.";

        return RedirectToAction(nameof(Inspect), new { bookingId = model.BookingId });
    }

    [HttpGet]
    public async Task<IActionResult> Inspect(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var records = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (records?.Handover is null || records.VehicleReturn is null)
        {
            TempData["ErrorMessage"] =
                "Cần có cả biên bản giao xe và biên bản trả xe để đối chiếu.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        var refundPayment = booking.Payments
            .Where(payment => payment.Type == PaymentType.Refund)
            .OrderByDescending(payment => payment.PaymentId)
            .FirstOrDefault();

        var model = new ReturnInspectionViewModel
        {
            BookingId = booking.BookingId,
            CustomerName = booking.CustomerName,
            VehicleName = booking.VehicleName,
            LicensePlate = booking.LicensePlate,
            Status = booking.Status,
            DepositAmount = booking.DepositAmount,
            AdditionalAmount = booking.AdditionalAmount,
            AdditionalChargePaid = booking.AdditionalChargePaid,
            RefundStatus = refundPayment?.Status,
            RefundAmount = refundPayment?.Amount ?? 0m,
            AdditionalCharges = booking.AdditionalCharges,
            Handover = new InspectionSnapshotViewModel
            {
                RecordedAt = records.Handover.HandoverAt,
                Mileage = records.Handover.Mileage,
                FuelLevel = records.Handover.FuelLevel,
                Notes = records.Handover.Notes,
                ImagePaths = SplitImagePaths(records.Handover.ImagePaths)
            },
            Return = new InspectionSnapshotViewModel
            {
                RecordedAt = records.VehicleReturn.ReturnedAt,
                Mileage = records.VehicleReturn.Mileage,
                FuelLevel = records.VehicleReturn.FuelLevel,
                HasDamage = records.VehicleReturn.HasDamage,
                Notes = records.VehicleReturn.Notes,
                ImagePaths = SplitImagePaths(records.VehicleReturn.ImagePaths)
            }
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCharge(
        AddChargeViewModel model,
        CancellationToken cancellationToken)
    {
        var result = ModelState.IsValid
            ? await _returnService.AddChargeAsync(
                new AddChargeRequest(
                    model.BookingId,
                    model.ChargeType,
                    model.Description,
                    model.Amount),
                cancellationToken)
            : OperationResult.Failure("Thông tin phụ phí không hợp lệ.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã thêm phụ phí và cập nhật tổng tiền."
            : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "AddCharge",
                nameof(AdditionalCharge),
                model.BookingId,
                $"Thêm phụ phí {model.ChargeType} cho đơn #{model.BookingId}: {model.Amount:N0} đồng. {model.Description}",
                cancellationToken);
        }

        return RedirectToAction("Details", "AdminBookings", new { id = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCharge(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken)
    {
        var result = await _returnService.RemoveChargeAsync(
            bookingId,
            additionalChargeId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xóa phụ phí."
            : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "RemoveCharge",
                nameof(AdditionalCharge),
                additionalChargeId,
                $"Xóa phụ phí #{additionalChargeId} khỏi đơn #{bookingId}.",
                cancellationToken);
        }

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(
        CompleteBookingViewModel model,
        bool reviewConfirmed,
        CancellationToken cancellationToken)
    {
        if (!reviewConfirmed)
        {
            TempData["ErrorMessage"] =
                "Vui lòng đối chiếu biên bản giao và trả trước khi kết thúc kiểm tra.";
            return RedirectToAction(nameof(Inspect), new { bookingId = model.BookingId });
        }

        var records = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == model.BookingId, cancellationToken);

        if (records?.Handover is null || records.VehicleReturn is null)
        {
            TempData["ErrorMessage"] = "Thiếu biên bản giao hoặc trả xe.";
            return RedirectToAction(nameof(Inspect), new { bookingId = model.BookingId });
        }

        if (!HasSignedCopy(records.Handover.ImagePaths, HandoverSignedMarker) ||
            !HasSignedCopy(records.VehicleReturn.ImagePaths, ReturnSignedMarker))
        {
            TempData["ErrorMessage"] =
                "Cần tải đủ bản giao và bản trả có chữ ký của khách + đại diện SmartCar trước khi kết thúc chuyến.";
            return RedirectToAction(nameof(Inspect), new { bookingId = model.BookingId });
        }

        var result = await _returnService.CompleteAsync(
            model.BookingId,
            model.RequiresMaintenance,
            model.MaintenanceNote,
            cancellationToken);

        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToAction(nameof(Inspect), new { bookingId = model.BookingId });
        }

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == model.BookingId, cancellationToken);

        var awaitingRefund = booking?.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                payment.Status == PaymentStatus.AwaitingRefund)
            .OrderByDescending(payment => payment.PaymentId)
            .FirstOrDefault();

        if (booking is not null && awaitingRefund is not null)
        {
            booking.Status = BookingStatus.AwaitingRefund;

            var completionNotification = await _dbContext.Notifications
                .Where(notification =>
                    notification.UserId == booking.CustomerId &&
                    notification.Title == "Đơn thuê đã hoàn tất" &&
                    notification.Message.Contains($"#{booking.BookingId}"))
                .OrderByDescending(notification => notification.NotificationId)
                .FirstOrDefaultAsync(cancellationToken);

            if (completionNotification is not null)
            {
                completionNotification.Title = "Đã kiểm tra xe - chờ hoàn cọc";
                completionNotification.Message =
                    $"Đơn #{booking.BookingId} đã kiểm tra xong. Đang chờ hoàn cọc {awaitingRefund.Amount:N0} đồng.";
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            TempData["SuccessMessage"] = $"Đã đối chiếu hồ sơ. Chờ hoàn cọc {awaitingRefund.Amount:N0} đ.";
        }
        else
        {
            TempData["SuccessMessage"] = "Đã đối chiếu và hoàn tất chuyến thuê.";
        }

        await WriteAuditAsync(
            "CompleteBooking",
            nameof(Booking),
            model.BookingId,
            awaitingRefund is not null
                ? $"Kết thúc kiểm tra đơn #{model.BookingId}; chuyển sang chờ hoàn cọc."
                : $"Hoàn tất đơn #{model.BookingId} sau khi đủ hồ sơ giao-trả có chữ ký.",
            cancellationToken);

        if (awaitingRefund is not null)
        {
            return RedirectToAction(
                "Index",
                "AdminPayments",
                new
                {
                    section = "refund",
                    status = PaymentStatus.AwaitingRefund,
                    type = PaymentType.Refund
                });
        }

        return RedirectToAction("Details", "AdminTripRecords", new { id = model.BookingId });
    }

    private async Task PopulateHandoverBaselineAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var handover = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (handover is not null)
        {
            SetHandoverBaseline(handover);
        }
    }

    private void SetHandoverBaseline(VehicleHandover handover)
    {
        ViewBag.HandoverMileage = handover.Mileage;
        ViewBag.HandoverFuelLevel = handover.FuelLevel;
        ViewBag.HandoverAt = handover.HandoverAt;
        ViewBag.HandoverIncludedKilometers = handover.IncludedKilometers;
        ViewBag.HandoverExcessKmFeePerKm = handover.ExcessKmFeePerKm;
    }

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages = images.Where(file => file.Length > 0).ToList();
        if (selectedImages.Count == 0)
        {
            ModelState.AddModelError(
                nameof(ReturnViewModel.Images),
                "Vui lòng tải ít nhất một ảnh tình trạng xe khi trả.");
            return;
        }

        if (selectedImages.Count > MaximumImages)
        {
            ModelState.AddModelError(
                nameof(ReturnViewModel.Images),
                $"Chỉ được tải tối đa {MaximumImages} ảnh khi trả xe.");
        }

        foreach (var image in selectedImages)
        {
            var error = await ImageFileValidator.ValidateAsync(
                image,
                MaximumImageBytes,
                cancellationToken);

            if (error is not null)
            {
                ModelState.AddModelError(
                    nameof(ReturnViewModel.Images),
                    $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/returns/{bookingId}";
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

    private static IReadOnlyList<string> SplitImagePaths(string? imagePaths) =>
        string.IsNullOrWhiteSpace(imagePaths)
            ? Array.Empty<string>()
            : imagePaths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    private static bool HasSignedCopy(string? imagePaths, string marker) =>
        SplitImagePaths(imagePaths)
            .Any(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private Task WriteAuditAsync(
        string action,
        string entityName,
        int entityId,
        string description,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return _auditService.WriteAsync(
            adminId,
            action,
            entityName,
            entityId.ToString(),
            description,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }

    private void AddErrors(IEnumerable<string> errors)
    {
        foreach (var error in errors)
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }
}
