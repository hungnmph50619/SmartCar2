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

[Authorize(Roles = RoleNames.Staff)]
public sealed class ReturnsController : Controller
{
    private const int MaximumImages = 25;
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
            return RedirectToBookingDetails(bookingId);
        }

        var handover = await _dbContext.VehicleHandovers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe để đối chiếu.";
            return RedirectToBookingDetails(bookingId);
        }

        SetHandoverBaseline(handover);
        var model = new ReturnViewModel
        {
            BookingId = bookingId,
            ReturnedAt = DateTime.Now,
            Mileage = handover.Mileage,
            AccessoryStatus = "Đủ"
        };

        if (!await PopulateVerifiedIdentityAsync(model, booking, cancellationToken))
        {
            TempData["ErrorMessage"] =
                "Không tìm thấy CCCD đã được Admin xác minh của khách đứng tên đơn. Vui lòng kiểm tra hồ sơ trước khi nhận xe trả.";
            return RedirectToBookingDetails(bookingId);
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ReturnViewModel model,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(model.BookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented || booking.HasReturn)
        {
            TempData["ErrorMessage"] = booking.HasReturn
                ? "Đơn đã có biên bản trả xe."
                : "Đơn không còn ở trạng thái đang thuê.";
            return RedirectToBookingDetails(model.BookingId);
        }

        ModelState.Remove(nameof(ReturnViewModel.CustomerId));
        ModelState.Remove(nameof(ReturnViewModel.VerifiedCustomerName));
        ModelState.Remove(nameof(ReturnViewModel.VerifiedCitizenId));
        ModelState.Remove(nameof(ReturnViewModel.ReturnedAt));
        model.ReturnedAt = DateTime.Now;

        var identityFailures = new List<string>();
        if (!await PopulateVerifiedIdentityAsync(model, booking, cancellationToken))
        {
            ModelState.AddModelError(
                string.Empty,
                "Hồ sơ CCCD đã xác minh của khách không còn khả dụng. Vui lòng dừng nhận xe trả và kiểm tra lại hồ sơ.");
            identityFailures.Add("không có CCCD KYC hợp lệ");
        }

        if (!model.OriginalCitizenIdChecked)
        {
            identityFailures.Add("chưa kiểm tra CCCD bản gốc");
        }

        if (!model.ReturnerIdentityCheckedInPerson)
        {
            identityFailures.Add("chưa xác nhận đúng người đang trực tiếp trả xe");
        }

        await ValidateIdentityFaceSessionAsync(model, booking, identityFailures, cancellationToken);

        var evidenceFiles = BuildEvidenceFiles(model);
        await ValidateImagesAsync(
            evidenceFiles,
            model.DamageImages,
            model.Images,
            model.HasDamage,
            cancellationToken);

        if (identityFailures.Count > 0)
        {
            await WriteAuditAsync(
                "FailedReturnIdentityCheck",
                nameof(Booking),
                model.BookingId,
                $"Nhận xe trả chưa đạt điều kiện danh tính: {string.Join("; ", identityFailures.Distinct())}. Biên bản chưa được tạo.",
                cancellationToken);
        }

        if (!ModelState.IsValid)
        {
            await PopulateHandoverBaselineAsync(model.BookingId, cancellationToken);
            return View(model);
        }

        var staffId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(staffId))
        {
            return Challenge();
        }

        IReadOnlyList<string> imagePaths;
        try
        {
            imagePaths = await SaveImagesAsync(
                model.BookingId,
                evidenceFiles,
                model.DamageImages,
                model.Images,
                cancellationToken);
        }
        catch
        {
            ModelState.AddModelError(
                nameof(ReturnViewModel.Images),
                "Không thể lưu ảnh trả xe. Vui lòng thử lại.");
            await PopulateHandoverBaselineAsync(model.BookingId, cancellationToken);
            return View(model);
        }

        var result = await _returnService.CreateAsync(
            new CreateReturnRequest(
                model.BookingId,
                model.ReturnedAt,
                model.Mileage,
                model.FuelLevel,
                model.ExteriorCondition,
                model.InteriorCondition,
                model.AccessoryStatus,
                model.HasDamage,
                string.Join(';', imagePaths),
                model.Notes,
                staffId,
                model.IdentityFaceSessionId!.Value),
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
            $"Lập biên bản trả xe đơn #{model.BookingId}, {model.Mileage:N0} km, {imagePaths.Count} ảnh chứng cứ; " +
            $"đối chiếu CCCD bản gốc với KYC, chụp ảnh mặt người trả trực tiếp và consume phiên ảnh một lần trong cùng transaction; " +
            $"hư hỏng mới: {(model.HasDamage ? "Có" : "Không")}.",
            cancellationToken);

        TempData["SuccessMessage"] =
            "Đã lưu biên bản trả xe, ảnh mặt trực tiếp và kết quả xác minh người trả. Hãy in, ký và tải bản ký trước khi quyết toán.";

        return RedirectToAction("Details", "Staff", new { id = model.BookingId });
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
            return RedirectToBookingDetails(bookingId);
        }

        if (!AllStaffChecksCompleted(records.Handover, records.VehicleReturn))
        {
            TempData["ErrorMessage"] =
                "Chưa đủ điều kiện đối chiếu quyết toán. Phải xác minh đúng người nhận, người trả và bản ký giao + trả trước.";
            return RedirectToBookingDetails(bookingId);
        }

        var refundPayment = booking.Payments
            .Where(payment => payment.Type == PaymentType.Refund)
            .OrderByDescending(payment => payment.PaymentId)
            .FirstOrDefault();

        ViewBag.DepositHoldDays = DepositHoldPolicy.NormalizeDays(records.DepositHoldDaysApplied);
        ViewBag.DepositEligibleAt = DepositHoldPolicy.CalculateEligibleAt(
            records.VehicleReturn.ReturnedAt, records.DepositHoldDaysApplied);

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
            HandoverIdentityVerified = records.Handover.CustomerIdentityVerified,
            HandoverSignedDocumentVerified = records.Handover.SignedDocumentVerified,
            ReturnIdentityVerified = records.VehicleReturn.CustomerIdentityVerified,
            ReturnSignedDocumentVerified = records.VehicleReturn.SignedDocumentVerified,
            Handover = new InspectionSnapshotViewModel
            {
                RecordedAt = records.Handover.HandoverAt,
                Mileage = records.Handover.Mileage,
                FuelLevel = records.Handover.FuelLevel,
                ExteriorCondition = records.Handover.ExteriorCondition,
                InteriorCondition = records.Handover.InteriorCondition,
                Accessories = records.Handover.Accessories,
                Notes = records.Handover.Notes,
                ImagePaths = SplitImagePaths(records.Handover.ImagePaths)
            },
            Return = new InspectionSnapshotViewModel
            {
                RecordedAt = records.VehicleReturn.ReturnedAt,
                Mileage = records.VehicleReturn.Mileage,
                FuelLevel = records.VehicleReturn.FuelLevel,
                ExteriorCondition = records.VehicleReturn.ExteriorCondition,
                InteriorCondition = records.VehicleReturn.InteriorCondition,
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
        var verified = await StaffDocumentChecksCompletedAsync(
            model.BookingId,
            cancellationToken);

        if (!verified)
        {
            TempData["ErrorMessage"] =
                "Chỉ được thêm phụ phí sau khi đã xác minh đầy đủ người nhận/trả và bản ký giao/trả.";
            return RedirectToBookingDetails(model.BookingId);
        }

        if (model.ChargeType == AdditionalChargeType.LateReturn ||
            model.ChargeType == AdditionalChargeType.ExcessMileage)
        {
            TempData["ErrorMessage"] =
                "Phí trả muộn và phí vượt km được hệ thống tự động tính, không được thêm thủ công.";
            return RedirectToAction(nameof(Inspect), new { bookingId = model.BookingId });
        }

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

        return RedirectToAction(nameof(Inspect), new { bookingId = model.BookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCharge(
        int bookingId,
        int additionalChargeId,
        CancellationToken cancellationToken)
    {
        var verified = await StaffDocumentChecksCompletedAsync(
            bookingId,
            cancellationToken);

        if (!verified)
        {
            TempData["ErrorMessage"] =
                "Chưa hoàn thành đủ bước xác minh nên không được sửa phụ phí.";
            return RedirectToBookingDetails(bookingId);
        }

        var chargeType = await _dbContext.AdditionalCharges
            .AsNoTracking()
            .Where(item =>
                item.AdditionalChargeId == additionalChargeId &&
                item.VehicleReturn.BookingId == bookingId)
            .Select(item => (AdditionalChargeType?)item.ChargeType)
            .FirstOrDefaultAsync(cancellationToken);

        if (chargeType == null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy phụ phí.";
            return RedirectToAction(nameof(Inspect), new { bookingId });
        }

        if (chargeType == AdditionalChargeType.LateReturn ||
            chargeType == AdditionalChargeType.ExcessMileage)
        {
            TempData["ErrorMessage"] =
                "Phí trả muộn và phí vượt km do hệ thống tự tính nên không thể xóa thủ công.";
            return RedirectToAction(nameof(Inspect), new { bookingId });
        }

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

        return RedirectToAction(nameof(Inspect), new { bookingId });
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

        if (!AllStaffChecksCompleted(records.Handover, records.VehicleReturn))
        {
            TempData["ErrorMessage"] =
                "Chưa đủ điều kiện kết thúc: phải đối chiếu đúng người nhận/trả và xác nhận bản ký giao + trả là hợp lệ.";
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

        var completionState = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == model.BookingId)
            .Select(item => new
            {
                item.Status,
                AwaitingRefundAmount = item.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.Refund &&
                        payment.Status == PaymentStatus.AwaitingRefund)
                    .Sum(payment => (decimal?)payment.Amount) ?? 0m
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (completionState is null)
        {
            TempData["ErrorMessage"] =
                "Không tìm thấy trạng thái đơn sau khi quyết toán.";
            return RedirectToBookingDetails(model.BookingId);
        }

        var awaitingRefund =
            completionState.Status == BookingStatus.AwaitingRefund &&
            completionState.AwaitingRefundAmount > 0;

        TempData["SuccessMessage"] = awaitingRefund
            ? $"Đã đối chiếu hồ sơ. Chờ Admin duyệt hoàn {completionState.AwaitingRefundAmount:N0} đ."
            : "Đã đối chiếu và hoàn tất chuyến thuê.";

        await WriteAuditAsync(
            "CompleteBooking",
            nameof(Booking),
            model.BookingId,
            awaitingRefund
                ? $"Kết thúc kiểm tra đơn #{model.BookingId}; chờ duyệt hoàn {completionState.AwaitingRefundAmount:N0} đồng."
                : $"Hoàn tất đơn #{model.BookingId} sau khi đủ hồ sơ giao-trả có chữ ký.",
            cancellationToken);

        return RedirectToBookingDetails(model.BookingId);
    }

    private async Task<bool> PopulateVerifiedIdentityAsync(
        ReturnViewModel model,
        BookingDetailsDto booking,
        CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == booking.CustomerId && user.IsActive)
            .Select(user => new { user.Id, user.FullName })
            .FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            return false;
        }

        var citizen = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(document =>
                document.CustomerId == booking.CustomerId &&
                document.DocumentType == DocumentTypes.CitizenId &&
                document.Status == DocumentStatus.Verified,
                cancellationToken);
        if (citizen is null)
        {
            return false;
        }

        model.CustomerId = customer.Id;
        model.VerifiedCustomerName = customer.FullName;
        model.VerifiedCitizenId = citizen.DocumentNumber;
        return true;
    }

    private async Task ValidateIdentityFaceSessionAsync(
        ReturnViewModel model,
        BookingDetailsDto booking,
        ICollection<string> identityFailures,
        CancellationToken cancellationToken)
    {
        if (!model.IdentityFaceSessionId.HasValue)
        {
            identityFailures.Add("chưa chụp ảnh mặt người trả trực tiếp");
            return;
        }

        var valid = await _dbContext.Set<IdentityCaptureSession>()
            .AsNoTracking()
            .AnyAsync(session =>
                session.IdentityCaptureSessionId == model.IdentityFaceSessionId.Value &&
                session.Purpose == IdentityCapturePurposes.Return &&
                session.BookingId == booking.BookingId &&
                session.TargetCustomerId == booking.CustomerId &&
                session.CompletedAt.HasValue &&
                !session.ConsumedAt.HasValue &&
                session.ImagePath != null,
                cancellationToken);

        if (!valid)
        {
            ModelState.AddModelError(
                nameof(ReturnViewModel.IdentityFaceSessionId),
                "Ảnh mặt người trả không hợp lệ, không thuộc đúng đơn/khách hoặc đã được sử dụng. Vui lòng chụp lại.");
            identityFailures.Add("ảnh mặt trực tiếp không hợp lệ");
        }
    }

    private static bool AllStaffChecksCompleted(
        VehicleHandover handover,
        VehicleReturn vehicleReturn) =>
        handover.CustomerIdentityVerified &&
        handover.SignedDocumentVerified &&
        vehicleReturn.CustomerIdentityVerified &&
        vehicleReturn.SignedDocumentVerified &&
        HasSignedCopy(handover.ImagePaths, HandoverSignedMarker) &&
        HasSignedCopy(vehicleReturn.ImagePaths, ReturnSignedMarker);

    private async Task<bool> StaffDocumentChecksCompletedAsync(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(
                item => item.BookingId == bookingId,
                cancellationToken);

        if (booking?.Handover is null || booking.VehicleReturn is null)
        {
            return false;
        }

        return AllStaffChecksCompleted(booking.Handover, booking.VehicleReturn);
    }

    private IActionResult RedirectToBookingDetails(int bookingId) =>
        RedirectToAction("Details", "Staff", new { id = bookingId });

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
        ViewBag.HandoverExteriorCondition = handover.ExteriorCondition;
        ViewBag.HandoverInteriorCondition = handover.InteriorCondition;
        ViewBag.HandoverAccessories = handover.Accessories;
    }

    private static IReadOnlyList<(string Label, string FieldName, IFormFile? File)> BuildEvidenceFiles(
        ReturnViewModel model) =>
        new (string, string, IFormFile?)[]
        {
            ("front", nameof(ReturnViewModel.FrontImage), model.FrontImage),
            ("rear", nameof(ReturnViewModel.RearImage), model.RearImage),
            ("left", nameof(ReturnViewModel.LeftImage), model.LeftImage),
            ("right", nameof(ReturnViewModel.RightImage), model.RightImage),
            ("interior", nameof(ReturnViewModel.InteriorImage), model.InteriorImage),
            ("odometer", nameof(ReturnViewModel.OdometerImage), model.OdometerImage),
            ("fuel", nameof(ReturnViewModel.FuelImage), model.FuelImage)
        };

    private async Task ValidateImagesAsync(
        IReadOnlyList<(string Label, string FieldName, IFormFile? File)> evidenceFiles,
        IReadOnlyCollection<IFormFile> damageImages,
        IReadOnlyCollection<IFormFile> otherImages,
        bool hasDamage,
        CancellationToken cancellationToken)
    {
        foreach (var evidence in evidenceFiles)
        {
            if (evidence.File is null || evidence.File.Length == 0)
            {
                ModelState.AddModelError(evidence.FieldName, "Cần ảnh này để đối chiếu với lúc giao.");
                continue;
            }

            await ValidateImageAsync(evidence.File, evidence.FieldName, cancellationToken);
        }

        var selectedDamageImages = damageImages.Where(file => file.Length > 0).ToList();
        var selectedOtherImages = otherImages.Where(file => file.Length > 0).ToList();

        if (hasDamage && selectedDamageImages.Count == 0)
        {
            ModelState.AddModelError(
                nameof(ReturnViewModel.DamageImages),
                "Đã đánh dấu hư hỏng thì phải có ảnh hư hỏng.");
        }

        if (evidenceFiles.Count + selectedDamageImages.Count + selectedOtherImages.Count > MaximumImages)
        {
            ModelState.AddModelError(
                nameof(ReturnViewModel.Images),
                $"Tổng số ảnh tối đa là {MaximumImages}.");
        }

        foreach (var image in selectedDamageImages)
        {
            await ValidateImageAsync(image, nameof(ReturnViewModel.DamageImages), cancellationToken);
        }

        foreach (var image in selectedOtherImages)
        {
            await ValidateImageAsync(image, nameof(ReturnViewModel.Images), cancellationToken);
        }

        var duplicateCandidates = evidenceFiles
            .Select(item => (item.FieldName, item.File))
            .Concat(selectedDamageImages.Select(file =>
                (nameof(ReturnViewModel.DamageImages), (IFormFile?)file)))
            .Concat(selectedOtherImages.Select(file =>
                (nameof(ReturnViewModel.Images), (IFormFile?)file)));

        var duplicatesByField = await ImageFileValidator.FindDuplicateContentFieldsAsync(
            duplicateCandidates,
            cancellationToken);
        foreach (var duplicate in duplicatesByField)
        {
            ModelState.AddModelError(
                duplicate.Key,
                "Không được dùng cùng một ảnh cho nhiều vị trí/chứng cứ. Ảnh trùng nội dung: " +
                string.Join(", ", duplicate.Value));
        }
    }

    private async Task ValidateImageAsync(
        IFormFile image,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var error = await ImageFileValidator.ValidateAsync(
            image,
            MaximumImageBytes,
            cancellationToken);
        if (error is not null)
        {
            ModelState.AddModelError(fieldName, $"{image.FileName}: {error}");
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IReadOnlyList<(string Label, string FieldName, IFormFile? File)> evidenceFiles,
        IEnumerable<IFormFile> damageImages,
        IEnumerable<IFormFile> otherImages,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/returns/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var paths = new List<string>();
        try
        {
            foreach (var evidence in evidenceFiles.Where(item => item.File is { Length: > 0 }))
            {
                paths.Add(await SaveOneImageAsync(
                    folder,
                    relativeFolder,
                    evidence.Label,
                    evidence.File!,
                    cancellationToken));
            }

            foreach (var image in damageImages.Where(file => file.Length > 0))
            {
                paths.Add(await SaveOneImageAsync(
                    folder,
                    relativeFolder,
                    "damage",
                    image,
                    cancellationToken));
            }

            foreach (var image in otherImages.Where(file => file.Length > 0))
            {
                paths.Add(await SaveOneImageAsync(
                    folder,
                    relativeFolder,
                    "other",
                    image,
                    cancellationToken));
            }

            return paths;
        }
        catch
        {
            DeleteSavedImages(paths);
            throw;
        }
    }

    private static async Task<string> SaveOneImageAsync(
        string folder,
        string relativeFolder,
        string label,
        IFormFile image,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var fileName = $"{label}-{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(folder, fileName);

        await using var stream = System.IO.File.Create(fullPath);
        await image.CopyToAsync(stream, cancellationToken);
        return $"/{relativeFolder}/{fileName}";
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
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return _auditService.WriteAsync(
            actorId,
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
