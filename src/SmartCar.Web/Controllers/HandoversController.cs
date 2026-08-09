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
    private const int MaximumImages = 10;

    private const long MaximumImageBytes =
        5 * 1024 * 1024;


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
        _handoverService =
            handoverService;

        _bookingService =
            bookingService;

        _auditService =
            auditService;

        _environment =
            environment;
    }


    // ================================================================
    // GET: LẬP BIÊN BẢN GIAO XE
    // ================================================================

    [HttpGet]
    public async Task<IActionResult> Create(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking =
            await _bookingService
                .GetAdminBookingAsync(
                    bookingId,
                    cancellationToken);


        if (booking is null)
        {
            return NotFound();
        }


        // Chỉ được lập biên bản khi xe đã được
        // admin đánh dấu ReadyForPickup.
        if (booking.Status !=
            BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] =
                "Chỉ đơn đang sẵn sàng giao xe mới được lập biên bản bàn giao.";

            return RedirectToAction(
                "Details",
                "AdminBookings",
                new
                {
                    id = bookingId
                });
        }


        // Không thể giao xe sau thời điểm phải trả.
        if (DateTime.Now >=
            booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao xe.";

            return RedirectToAction(
                "Details",
                "AdminBookings",
                new
                {
                    id = bookingId
                });
        }


        var model =
            new HandoverViewModel
            {
                BookingId =
                    bookingId,

                HandoverAt =
                    DateTime.Now >
                    booking.PickupDate

                        ? DateTime.Now

                        : booking.PickupDate,

                IncludedKilometers =
                    booking.NumberOfDays
                    *
                    RentalPolicy.IncludedKilometersPerDay,

                ExcessKmFeePerKm =
                    RentalPolicy.ExcessKilometerFee,

                LateReturnFeeMultiplier =
                    RentalPolicy.LateReturnFeeMultiplier,

                TrafficFineTerms =
                    RentalPolicy.TrafficFineTerms,

                DamageCompensationTerms =
                    RentalPolicy.DamageCompensationTerms
            };


        return View(
            model);
    }


    // ================================================================
    // POST: XÁC NHẬN GIAO XE
    // ================================================================

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HandoverViewModel model,
        CancellationToken cancellationToken)
    {
        // ============================================================
        // 1. LẤY LẠI BOOKING TỪ DATABASE
        // ============================================================

        var booking =
            await _bookingService
                .GetAdminBookingAsync(
                    model.BookingId,
                    cancellationToken);


        if (booking is null)
        {
            return NotFound();
        }


        // ============================================================
        // 2. KIỂM TRA TRẠNG THÁI ĐƠN
        // ============================================================

        if (booking.Status !=
            BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] =
                "Đơn không còn ở trạng thái sẵn sàng giao xe.";

            return RedirectToAction(
                "Details",
                "AdminBookings",
                new
                {
                    id = model.BookingId
                });
        }


        if (DateTime.Now >=
            booking.ReturnDate)
        {
            TempData["ErrorMessage"] =
                "Đã đến hoặc quá thời gian trả xe, không thể lập biên bản giao xe.";

            return RedirectToAction(
                "Details",
                "AdminBookings",
                new
                {
                    id = model.BookingId
                });
        }


        // ============================================================
        // 3. XÓA VALIDATION CHO CÁC FIELD CHÍNH SÁCH
        //
        // Các field này KHÔNG được lấy từ browser.
        //
        // Nếu để ModelState cũ:
        // "5000,00" và "1,50" có thể bị ASP.NET/jQuery
        // coi là decimal không hợp lệ.
        // ============================================================

        ModelState.Remove(
            nameof(
                HandoverViewModel.IncludedKilometers));

        ModelState.Remove(
            nameof(
                HandoverViewModel.ExcessKmFeePerKm));

        ModelState.Remove(
            nameof(
                HandoverViewModel.LateReturnFeeMultiplier));

        ModelState.Remove(
            nameof(
                HandoverViewModel.TrafficFineTerms));

        ModelState.Remove(
            nameof(
                HandoverViewModel.DamageCompensationTerms));


        // ============================================================
        // 4. TÍNH LẠI CHÍNH SÁCH TỪ SERVER
        //
        // Không tin dữ liệu tiền/phí từ client.
        // ============================================================

        model.IncludedKilometers =
            booking.NumberOfDays
            *
            RentalPolicy.IncludedKilometersPerDay;


        model.ExcessKmFeePerKm =
            RentalPolicy.ExcessKilometerFee;


        model.LateReturnFeeMultiplier =
            RentalPolicy.LateReturnFeeMultiplier;


        model.TrafficFineTerms =
            RentalPolicy.TrafficFineTerms;


        model.DamageCompensationTerms =
            RentalPolicy.DamageCompensationTerms;


        // ============================================================
        // 5. VALIDATE ẢNH
        // ============================================================

        await ValidateImagesAsync(
            model.Images,
            cancellationToken);


        // ============================================================
        // 6. NẾU CÒN LỖI -> TRẢ LẠI FORM
        // ============================================================

        if (!ModelState.IsValid)
        {
            return View(
                model);
        }


        // ============================================================
        // 7. LƯU ẢNH
        // ============================================================

        IReadOnlyList<string> imagePaths;


        try
        {
            imagePaths =
                await SaveImagesAsync(
                    model.BookingId,
                    model.Images,
                    cancellationToken);
        }
        catch
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                "Không thể lưu ảnh bàn giao. Vui lòng kiểm tra lại file ảnh và thử lại.");

            return View(
                model);
        }


        // ============================================================
        // 8. GỌI SERVICE TẠO BIÊN BẢN
        // ============================================================

        var result =
            await _handoverService
                .CreateAsync(
                    new CreateHandoverRequest(
                        model.BookingId,

                        model.HandoverAt,

                        model.Mileage,

                        model.FuelLevel,

                        model.ExteriorCondition,

                        model.InteriorCondition,

                        model.Accessories,

                        string.Join(
                            ';',
                            imagePaths),

                        // Các giá trị này đã được lấy lại
                        // từ RentalPolicy ở phía server.
                        model.IncludedKilometers,

                        model.ExcessKmFeePerKm,

                        model.LateReturnFeeMultiplier,

                        model.TrafficFineTerms,

                        model.DamageCompensationTerms,

                        model.PenaltyPolicyAccepted,

                        model.Notes),

                    cancellationToken);


        // ============================================================
        // 9. SERVICE TỪ CHỐI
        // ============================================================

        if (!result.Succeeded)
        {
            DeleteSavedImages(
                imagePaths);


            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(
                    string.Empty,
                    error);
            }


            return View(
                model);
        }


        // ============================================================
        // 10. AUDIT LOG
        // ============================================================

        var adminId =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);


        await _auditService
            .WriteAsync(
                adminId,

                "CreateHandover",

                nameof(VehicleHandover),

                model.BookingId.ToString(),

                $"Lập biên bản giao xe cho đơn " +
                $"#{model.BookingId}, " +
                $"số km {model.Mileage}, " +
                $"{imagePaths.Count} ảnh.",

                ipAddress:
                    HttpContext.Connection
                        .RemoteIpAddress?
                        .ToString(),

                cancellationToken:
                    cancellationToken);


        // ============================================================
        // 11. THÀNH CÔNG
        // ============================================================

        TempData["SuccessMessage"] =
            "Đã lập biên bản giao xe. " +
            "Đơn đã chuyển sang trạng thái Đang thuê.";


        return RedirectToAction(
            "Details",
            "AdminBookings",
            new
            {
                id = model.BookingId
            });
    }


    // ================================================================
    // IN BIÊN BẢN
    // ================================================================

    [HttpGet]
    public async Task<IActionResult> Print(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking =
            await _bookingService
                .GetAdminBookingAsync(
                    bookingId,
                    cancellationToken);


        if (booking is null)
        {
            return NotFound();
        }


        if (booking.Handover is null)
        {
            TempData["ErrorMessage"] =
                "Đơn chưa có biên bản giao xe để in.";


            return RedirectToAction(
                "Details",
                "AdminBookings",
                new
                {
                    id = bookingId
                });
        }


        return View(
            "Print",
            booking);
    }


    // ================================================================
    // VALIDATE ẢNH
    // ================================================================

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selectedImages =
            images
                .Where(file =>
                    file.Length > 0)
                .ToList();


        if (selectedImages.Count == 0)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                "Vui lòng tải ít nhất một ảnh tình trạng xe khi bàn giao.");

            return;
        }


        if (selectedImages.Count >
            MaximumImages)
        {
            ModelState.AddModelError(
                nameof(HandoverViewModel.Images),
                $"Chỉ được tải tối đa {MaximumImages} ảnh bàn giao.");
        }


        foreach (var image in selectedImages)
        {
            var error =
                await ImageFileValidator
                    .ValidateAsync(
                        image,
                        MaximumImageBytes,
                        cancellationToken);


            if (error is not null)
            {
                ModelState.AddModelError(
                    nameof(HandoverViewModel.Images),
                    $"{image.FileName}: {error}");
            }
        }
    }


    // ================================================================
    // LƯU ẢNH
    // ================================================================

    private async Task<IReadOnlyList<string>>
        SaveImagesAsync(
            int bookingId,
            IEnumerable<IFormFile> images,
            CancellationToken cancellationToken)
    {
        var relativeFolder =
            $"uploads/handovers/{bookingId}";


        var folder =
            Path.Combine(
                _environment.WebRootPath,
                relativeFolder);


        Directory.CreateDirectory(
            folder);


        var paths =
            new List<string>();


        try
        {
            foreach (
                var image
                in images.Where(file =>
                    file.Length > 0))
            {
                var extension =
                    Path.GetExtension(
                            image.FileName)
                        .ToLowerInvariant();


                var fileName =
                    $"{Guid.NewGuid():N}{extension}";


                var fullPath =
                    Path.Combine(
                        folder,
                        fileName);


                await using var stream =
                    System.IO.File.Create(
                        fullPath);


                await image.CopyToAsync(
                    stream,
                    cancellationToken);


                paths.Add(
                    $"/{relativeFolder}/{fileName}");
            }


            return paths;
        }
        catch
        {
            // Nếu lưu 4 ảnh mà ảnh thứ 5 lỗi,
            // xóa các ảnh đã lưu trước đó.
            DeleteSavedImages(
                paths);

            throw;
        }
    }


    // ================================================================
    // XÓA ẢNH ĐÃ LƯU KHI GIAO DỊCH KHÔNG THÀNH CÔNG
    // ================================================================

    private void DeleteSavedImages(
        IEnumerable<string> imagePaths)
    {
        foreach (var imagePath in imagePaths)
        {
            var fullPath =
                Path.Combine(
                    _environment.WebRootPath,

                    imagePath
                        .TrimStart('/')
                        .Replace(
                            '/',
                            Path.DirectorySeparatorChar));


            if (System.IO.File.Exists(
                    fullPath))
            {
                System.IO.File.Delete(
                    fullPath);
            }
        }
    }
}