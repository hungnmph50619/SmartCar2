using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Extensions;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminExtensionsController : Controller
{
    private const long MaximumEvidenceImageBytes = 5 * 1024 * 1024;
    private const string ForceMajeureMarker = "[FORCE_MAJEURE]";

    private static readonly BookingStatus[] BlockingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly IExtensionService _extensionService;
    private readonly IBookingService _bookingService;
    private readonly IBookingOperationService _bookingOperationService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;
    private readonly ISecureDocumentStorage _secureDocumentStorage;

    public AdminExtensionsController(
        IExtensionService extensionService,
        IBookingService bookingService,
        IBookingOperationService bookingOperationService,
        IAuditService auditService,
        ApplicationDbContext dbContext,
        ISecureDocumentStorage secureDocumentStorage)
    {
        _extensionService = extensionService;
        _bookingService = bookingService;
        _bookingOperationService = bookingOperationService;
        _auditService = auditService;
        _dbContext = dbContext;
        _secureDocumentStorage = secureDocumentStorage;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var extensions = await _extensionService.GetPendingExtensionsAsync(cancellationToken);
        var conflictResolutions = new Dictionary<int, ExtensionConflictResolutionViewModel>();

        foreach (var item in extensions.Where(item =>
                     item.IsForceMajeure &&
                     item.HasScheduleConflict &&
                     item.ConflictingBookingId.HasValue))
        {
            var resolution = await BuildConflictResolutionAsync(item, cancellationToken);
            if (resolution is not null)
            {
                conflictResolutions[item.BookingExtensionId] = resolution;
            }
        }

        ViewBag.ConflictResolutions = conflictResolutions;
        return View(extensions);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateForCustomer(
        int bookingId,
        DateTime requestedReturnDate,
        string? note,
        bool isForceMajeure,
        string? evidenceNote,
        string? customerLiveLocation,
        IFormFile? evidenceImage,
        CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["ErrorMessage"] = "Chỉ đơn đang thuê mới được ghi nhận yêu cầu gia hạn.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (requestedReturnDate <= booking.ReturnDate)
        {
            TempData["ErrorMessage"] = "Giờ trả mới phải sau giờ trả hiện tại.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (isForceMajeure)
        {
            if (string.IsNullOrWhiteSpace(note))
            {
                TempData["ErrorMessage"] = "Vui lòng ghi rõ lý do bất khả kháng.";
                return RedirectToAction(nameof(Index));
            }

            if (evidenceImage is null || evidenceImage.Length == 0)
            {
                TempData["ErrorMessage"] = "Bất khả kháng phải có ảnh minh chứng.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(customerLiveLocation))
            {
                TempData["ErrorMessage"] = "Bất khả kháng phải có vị trí hiện tại.";
                return RedirectToAction(nameof(Index));
            }
        }

        var (imagePath, imageError) = await SaveEvidenceImageAsync(
            bookingId,
            evidenceImage,
            cancellationToken);

        if (imageError is not null)
        {
            TempData["ErrorMessage"] = imageError;
            return RedirectToAction(nameof(Index));
        }

        var phoneNote = string.IsNullOrWhiteSpace(note)
            ? "SmartCar ghi nhận yêu cầu qua điện thoại."
            : $"SmartCar ghi nhận yêu cầu qua điện thoại. {note.Trim()}";
        var liveLocation = isForceMajeure && !string.IsNullOrWhiteSpace(customerLiveLocation)
            ? $"Vị trí trực tiếp khách cung cấp: {customerLiveLocation.Trim()}"
            : null;

        var result = await _extensionService.RequestAsync(
            booking.CustomerId,
            new RequestExtensionRequest(
                bookingId,
                requestedReturnDate,
                phoneNote,
                isForceMajeure,
                ComposeEvidence(evidenceNote, imagePath, liveLocation)),
            cancellationToken);

        if (!result.Succeeded)
        {
            DeleteSavedEvidenceImage(imagePath);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận yêu cầu gia hạn."
            : string.Join("; ", result.Errors);

        if (result.Succeeded)
        {
            var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            await _auditService.WriteAsync(
                adminId,
                "CreateExtensionForCustomer",
                nameof(BookingExtension),
                bookingId.ToString(),
                $"Ghi nhận yêu cầu gia hạn qua điện thoại cho đơn #{bookingId} đến {requestedReturnDate:dd/MM/yyyy HH:mm}. Loại: {(isForceMajeure ? "bất khả kháng" : "thông thường")}.",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);
        }

        return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveConflictingBooking(
        int extensionId,
        int replacementVehicleId,
        bool customerAccepted,
        CancellationToken cancellationToken)
    {
        if (!customerAccepted)
        {
            TempData["ErrorMessage"] = "Chỉ đổi xe sau khi khách B đồng ý xe và mức giá mới.";
            return RedirectToAction(nameof(Index));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .FirstOrDefaultAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        if (extension is null ||
            extension.Status != BookingExtensionStatus.Pending ||
            !IsForceMajeure(extension.CustomerNote))
        {
            TempData["ErrorMessage"] = "Yêu cầu gia hạn không còn hợp lệ để xử lý xung đột.";
            return RedirectToAction(nameof(Index));
        }

        var conflict = await FindConflictingBookingAsync(extension, cancellationToken);
        if (conflict is null)
        {
            TempData["ErrorMessage"] = "Đơn B không còn xung đột. Hãy tải lại trang.";
            return RedirectToAction(nameof(Index));
        }

        var replacement = await _dbContext.Vehicles
            .FirstOrDefaultAsync(item => item.VehicleId == replacementVehicleId, cancellationToken);

        if (replacement is null ||
            replacement.VehicleId == conflict.VehicleId ||
            replacement.Status is VehicleStatus.Maintenance or VehicleStatus.Inspection or VehicleStatus.Inactive)
        {
            TempData["ErrorMessage"] = "Xe thay thế không còn khả dụng.";
            return RedirectToAction(nameof(Index));
        }

        var conflictPreparation = TimeSpan.FromMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(conflict.PickupMethod));
        var conflictPickupBoundary = conflict.PickupDate - conflictPreparation;
        var conflictReturnWithStorePreparation = conflict.ReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.StorePickup));
        var conflictReturnWithDeliveryPreparation = conflict.ReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.Delivery));

        var replacementHasConflict = await _dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(item =>
                item.VehicleId == replacement.VehicleId &&
                item.BookingId != conflict.BookingId &&
                BlockingStatuses.Contains(item.Status) &&
                conflictPickupBoundary < item.ReturnDate &&
                (
                    item.PickupMethod == VehiclePickupMethod.Delivery
                        ? conflictReturnWithDeliveryPreparation > item.PickupDate
                        : conflictReturnWithStorePreparation > item.PickupDate
                ),
                cancellationToken);

        if (replacementHasConflict)
        {
            TempData["ErrorMessage"] = "Xe thay thế vừa có lịch trùng. Vui lòng chọn xe khác.";
            return RedirectToAction(nameof(Index));
        }

        var hasOpenIncident = await _dbContext.VehicleIncidents
            .AsNoTracking()
            .AnyAsync(item =>
                item.VehicleId == replacement.VehicleId &&
                item.Status != IncidentStatus.Resolved &&
                item.Status != IncidentStatus.Cancelled,
                cancellationToken);

        if (hasOpenIncident)
        {
            TempData["ErrorMessage"] = "Xe thay thế đang có sự cố chưa xử lý.";
            return RedirectToAction(nameof(Index));
        }

        var deliveryFee = Math.Max(0m, conflict.TotalAmount - conflict.RentalAmount - conflict.AdditionalAmount);
        var oldRentalAmount = conflict.RentalAmount;
        var oldDepositAmount = conflict.DepositAmount;
        var newRentalAmount = conflict.NumberOfDays * replacement.DailyPrice;
        var newDepositAmount = RentalPolicy.CalculateDeposit(newRentalAmount);
        var newRentalPaymentAmount = newRentalAmount + deliveryFee;
        var overallDifference = (newRentalAmount + newDepositAmount) - (oldRentalAmount + oldDepositAmount);

        var hasSettlementAwaitingConfirmation = conflict.Payments.Any(item =>
            item.Status == PaymentStatus.AwaitingConfirmation &&
            item.Type is PaymentType.Rental or PaymentType.Deposit or PaymentType.VehicleSwapAdjustment);
        if (hasSettlementAwaitingConfirmation)
        {
            TempData["ErrorMessage"] = "Đơn B đang có giao dịch trước giao xe chờ đối soát. Hãy xử lý giao dịch trước khi đổi xe.";
            return RedirectToAction(nameof(Index));
        }

        // Mọi khoản chênh lệch đổi xe Pending trước đó được tính theo chiếc xe cũ
        // và trở thành lỗi thời ngay khi đổi xe lần nữa. Đóng chúng trước khi tính lại từ ledger thực thu.
        FailPendingPayments(conflict.Payments, PaymentType.VehicleSwapAdjustment);

        var grossRentalPaid = conflict.Payments
            .Where(item =>
                item.Status == PaymentStatus.Paid &&
                item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
            .Sum(item => item.Amount);
        var rentalRefundPlanned = conflict.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.VehicleSwapRefund &&
                item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveRentalPaid = Math.Max(0m, grossRentalPaid - rentalRefundPlanned);

        var grossDepositPaid = conflict.Payments
            .Where(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.Paid)
            .Sum(item => item.Amount);
        var depositRefundPlanned = conflict.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.DepositRefund &&
                item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveDepositPaid = Math.Max(0m, grossDepositPaid - depositRefundPlanned);

        decimal rentalPaidDifference;
        if (grossRentalPaid > 0m)
        {
            rentalPaidDifference = newRentalPaymentAmount - effectiveRentalPaid;
            FailPendingPayments(conflict.Payments, PaymentType.Rental);
        }
        else
        {
            SetSinglePendingPaymentAmount(
                conflict.Payments,
                PaymentType.Rental,
                newRentalPaymentAmount);
            rentalPaidDifference = 0m;
        }

        decimal depositPaidDifference;
        if (grossDepositPaid > 0m)
        {
            depositPaidDifference = newDepositAmount - effectiveDepositPaid;
            FailPendingPayments(conflict.Payments, PaymentType.Deposit);
        }
        else
        {
            SetSinglePendingPaymentAmount(
                conflict.Payments,
                PaymentType.Deposit,
                newDepositAmount);
            depositPaidDifference = 0m;
        }

        var oldVehicleName = conflict.Vehicle.VehicleName;
        var oldVehicleId = conflict.VehicleId;
        conflict.VehicleId = replacement.VehicleId;
        conflict.Vehicle = replacement;
        conflict.DailyPrice = replacement.DailyPrice;
        conflict.RentalAmount = newRentalAmount;
        conflict.DepositAmount = newDepositAmount;
        conflict.TotalAmount = newRentalAmount + deliveryFee + conflict.AdditionalAmount;

        var amountToCollect = Math.Max(0m, rentalPaidDifference) + Math.Max(0m, depositPaidDifference);
        var rentalRefund = Math.Abs(Math.Min(0m, rentalPaidDifference));
        var depositRefund = Math.Abs(Math.Min(0m, depositPaidDifference));
        var totalRefund = rentalRefund + depositRefund;

        if (amountToCollect > 0)
        {
            conflict.Payments.Add(new Payment
            {
                Type = PaymentType.VehicleSwapAdjustment,
                Amount = amountToCollect,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending
            });

            if (conflict.Status == BookingStatus.ReadyForPickup)
            {
                // Đơn đã từng thanh toán đủ nhưng đổi xe phát sinh thu thêm:
                // quay về Paid để chặn bàn giao, KHÔNG đưa về PendingPayment vì hold ban đầu
                // có thể làm hết hạn cả booking đã được duyệt/chấp nhận trước đó.
                conflict.Status = BookingStatus.Paid;
            }
        }

        if (rentalRefund > 0)
        {
            conflict.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = rentalRefund,
                Method = PaymentMethods.VehicleSwapRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        if (depositRefund > 0)
        {
            conflict.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = depositRefund,
                Method = PaymentMethods.DepositRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        if (totalRefund > 0)
        {
            conflict.RefundAmount = conflict.Payments
                .Where(payment =>
                    payment.Type == PaymentType.Refund &&
                    BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
                .Sum(payment => payment.Amount);
            conflict.RefundReason = AppendText(
                conflict.RefundReason,
                $"Đổi xe: hoàn chênh lệch {totalRefund:N0} đồng.");
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = conflict.CustomerId,
            Title = "Đã xác nhận đổi xe",
            Message = amountToCollect > 0
                ? $"Đơn #{conflict.BookingId} đã đổi từ {oldVehicleName} sang {replacement.VehicleName} ({replacement.LicensePlate}). Cần thanh toán thêm {amountToCollect:N0} đồng."
                : totalRefund > 0
                    ? $"Đơn #{conflict.BookingId} đã đổi từ {oldVehicleName} sang {replacement.VehicleName} ({replacement.LicensePlate}). SmartCar sẽ hoàn chênh lệch {totalRefund:N0} đồng."
                    : overallDifference > 0
                        ? $"Đơn #{conflict.BookingId} đã đổi sang {replacement.VehicleName} ({replacement.LicensePlate}). Số cần thanh toán tăng {overallDifference:N0} đồng."
                        : overallDifference < 0
                            ? $"Đơn #{conflict.BookingId} đã đổi sang {replacement.VehicleName} ({replacement.LicensePlate}). Số cần thanh toán giảm {Math.Abs(overallDifference):N0} đồng."
                            : $"Đơn #{conflict.BookingId} đã đổi sang {replacement.VehicleName} ({replacement.LicensePlate}), không phát sinh chênh lệch."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "ResolveExtensionConflictByVehicleSwap",
            nameof(Booking),
            conflict.BookingId.ToString(),
            $"Đổi xe đơn #{conflict.BookingId} từ xe #{oldVehicleId} sang xe #{replacement.VehicleId}. Thu thêm: {amountToCollect:N0}; hoàn: {totalRefund:N0} đồng.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = amountToCollect > 0
            ? $"Đã đổi xe cho B. Cần thu thêm {amountToCollect:N0} đ."
            : totalRefund > 0
                ? $"Đã đổi xe cho B. Chờ hoàn {totalRefund:N0} đ."
                : "Đã đổi xe cho B. Số tiền đã được cập nhật.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelConflictingBooking(
        int extensionId,
        bool customerContacted,
        CancellationToken cancellationToken)
    {
        if (!customerContacted)
        {
            TempData["ErrorMessage"] = "Vui lòng xác nhận đã liên hệ khách B và khách không chấp nhận phương án đổi xe.";
            return RedirectToAction(nameof(Index));
        }

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
            .FirstOrDefaultAsync(item => item.BookingExtensionId == extensionId, cancellationToken);

        if (extension is null ||
            extension.Status != BookingExtensionStatus.Pending ||
            !IsForceMajeure(extension.CustomerNote))
        {
            TempData["ErrorMessage"] = "Yêu cầu gia hạn không còn hợp lệ để xử lý xung đột.";
            return RedirectToAction(nameof(Index));
        }

        var conflict = await FindConflictingBookingAsync(extension, cancellationToken);
        if (conflict is null)
        {
            TempData["ErrorMessage"] = "Đơn B không còn xung đột.";
            return RedirectToAction(nameof(Index));
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var conflictBookingId = conflict.BookingId;

        var cancelResult = await _bookingOperationService.CancelByAdminAsync(
            adminId,
            new CancelBookingRequest(
                conflictBookingId,
                $"SmartCar phải hủy do đơn #{extension.BookingId} phát sinh gia hạn bất khả kháng có minh chứng và khách không chấp nhận phương án đổi xe."),
            cancellationToken);

        if (!cancelResult.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", cancelResult.Errors);
            return RedirectToAction(nameof(Index));
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = extension.Booking.CustomerId,
            Title = "Đã xử lý đơn thuê kế tiếp",
            Message =
                $"Đơn #{extension.BookingId}: SmartCar đã xử lý xung đột với đơn #{conflictBookingId}. " +
                "Trường hợp bất khả kháng có minh chứng không bị tự động khấu trừ tiền cọc chỉ vì đơn kế tiếp phải hủy."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ResolveExtensionConflictByCancellation",
            nameof(Booking),
            conflictBookingId.ToString(),
            $"Hủy đơn #{conflictBookingId} do xung đột gia hạn bất khả kháng của đơn #{extension.BookingId}. " +
            $"Tổng khoản hoàn theo chính sách: {cancelResult.RefundAmount:N0} đồng. Không tạo bồi thường tự động và không khấu trừ cọc A.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            "Đã hủy đơn B và tạo khoản hoàn theo chính sách. Không khấu trừ cọc A vì đây là trường hợp bất khả kháng có minh chứng.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(
        int id,
        bool confirmConflictHandled,
        string? adminNote,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _extensionService.ApproveAsync(
            id,
            adminId,
            confirmConflictHandled,
            adminNote,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã duyệt gia hạn. Ngày trả mới có hiệu lực sau khi xác nhận thanh toán."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEvidence(
        int extensionId,
        string reason,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _extensionService.RequestMoreEvidenceAsync(
            extensionId,
            adminId,
            reason,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã yêu cầu khách bổ sung minh chứng."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        RejectExtensionViewModel model,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = ModelState.IsValid
            ? await _extensionService.RejectAsync(
                model.ExtensionId,
                adminId,
                model.Reason,
                cancellationToken)
            : SmartCar.Application.Common.OperationResult.Failure("Vui lòng nhập lý do từ chối.");

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã từ chối và thông báo cho khách."
            : string.Join("; ", result.Errors);
        return RedirectToAction(nameof(Index));
    }

    private async Task<ExtensionConflictResolutionViewModel?> BuildConflictResolutionAsync(
        ExtensionDto extension,
        CancellationToken cancellationToken)
    {
        if (!extension.ConflictingBookingId.HasValue)
        {
            return null;
        }

        var conflict = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .FirstOrDefaultAsync(item => item.BookingId == extension.ConflictingBookingId.Value, cancellationToken);

        if (conflict is null)
        {
            return null;
        }

        var customerName = await _dbContext.Users
            .AsNoTracking()
            .Where(item => item.Id == conflict.CustomerId)
            .Select(item => item.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Khách hàng";

        var alternatives = await GetAlternativeVehiclesAsync(conflict, cancellationToken);

        return new ExtensionConflictResolutionViewModel
        {
            ExtensionId = extension.BookingExtensionId,
            BookingId = conflict.BookingId,
            CustomerName = customerName,
            VehicleName = conflict.Vehicle.VehicleName,
            LicensePlate = conflict.Vehicle.LicensePlate,
            PickupDate = conflict.PickupDate,
            ReturnDate = conflict.ReturnDate,
            Alternatives = alternatives
        };
    }

    private async Task<IReadOnlyList<ExtensionAlternativeVehicleViewModel>> GetAlternativeVehiclesAsync(
        Booking conflict,
        CancellationToken cancellationToken)
    {
        var conflictPreparation = TimeSpan.FromMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(conflict.PickupMethod));
        var conflictPickupBoundary = conflict.PickupDate - conflictPreparation;
        var conflictReturnWithStorePreparation = conflict.ReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.StorePickup));
        var conflictReturnWithDeliveryPreparation = conflict.ReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.Delivery));

        var candidates = await _dbContext.Vehicles
            .AsNoTracking()
            .Where(vehicle =>
                vehicle.VehicleId != conflict.VehicleId &&
                vehicle.Status != VehicleStatus.Maintenance &&
                vehicle.Status != VehicleStatus.Inspection &&
                vehicle.Status != VehicleStatus.Inactive &&
                !_dbContext.Bookings.Any(booking =>
                    booking.VehicleId == vehicle.VehicleId &&
                    booking.BookingId != conflict.BookingId &&
                    BlockingStatuses.Contains(booking.Status) &&
                    conflictPickupBoundary < booking.ReturnDate &&
                    (
                        booking.PickupMethod == VehiclePickupMethod.Delivery
                            ? conflictReturnWithDeliveryPreparation > booking.PickupDate
                            : conflictReturnWithStorePreparation > booking.PickupDate
                    )) &&
                !_dbContext.VehicleIncidents.Any(incident =>
                    incident.VehicleId == vehicle.VehicleId &&
                    incident.Status != IncidentStatus.Resolved &&
                    incident.Status != IncidentStatus.Cancelled))
            .Select(vehicle => new ExtensionAlternativeVehicleViewModel
            {
                VehicleId = vehicle.VehicleId,
                VehicleName = vehicle.VehicleName,
                LicensePlate = vehicle.LicensePlate,
                DailyPrice = vehicle.DailyPrice,
                EstimatedRentalAmount = conflict.NumberOfDays * vehicle.DailyPrice,
                CurrentRentalAmount = conflict.RentalAmount
            })
            .ToListAsync(cancellationToken);

        return candidates
            .OrderBy(item => Math.Abs(item.PriceDifference))
            .ThenBy(item => item.DailyPrice)
            .ToList();
    }

    private async Task<Booking?> FindConflictingBookingAsync(
        BookingExtension extension,
        CancellationToken cancellationToken)
    {
        var storeBoundary = extension.RequestedReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.StorePickup));
        var deliveryBoundary = extension.RequestedReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.Delivery));

        return await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .Where(other =>
                other.VehicleId == extension.Booking.VehicleId &&
                other.BookingId != extension.BookingId &&
                BlockingStatuses.Contains(other.Status) &&
                other.ReturnDate > extension.OriginalReturnDate &&
                (
                    other.PickupMethod == VehiclePickupMethod.Delivery
                        ? other.PickupDate < deliveryBoundary
                        : other.PickupDate < storeBoundary
                ))
            .OrderBy(other => other.PickupDate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static void SetSinglePendingPaymentAmount(
        ICollection<Payment> payments,
        PaymentType type,
        decimal amount)
    {
        var pending = payments
            .Where(item => item.Type == type && item.Status == PaymentStatus.Pending)
            .OrderBy(item => item.PaymentId)
            .ToList();

        var primary = pending.FirstOrDefault();
        if (amount > 0m)
        {
            if (primary is null)
            {
                primary = new Payment
                {
                    Type = type,
                    Amount = amount,
                    Method = PaymentMethods.NotSelected,
                    Status = PaymentStatus.Pending
                };
                payments.Add(primary);
            }
            else
            {
                primary.Amount = amount;
                primary.Method = PaymentMethods.NotSelected;
                primary.PaidAt = null;
                primary.TransactionCode = null;
            }
        }

        foreach (var stale in pending.Where(item => item != primary))
        {
            stale.Status = PaymentStatus.Failed;
            stale.Method = PaymentMethods.NotSelected;
            stale.PaidAt = null;
            stale.TransactionCode = null;
        }
    }

    private static void FailPendingPayments(
        IEnumerable<Payment> payments,
        PaymentType type)
    {
        foreach (var payment in payments.Where(item =>
                     item.Type == type &&
                     item.Status == PaymentStatus.Pending))
        {
            payment.Status = PaymentStatus.Failed;
            payment.Method = PaymentMethods.NotSelected;
            payment.PaidAt = null;
            payment.TransactionCode = null;
        }
    }

    private async Task<(string? Path, string? Error)> SaveEvidenceImageAsync(
        int bookingId,
        IFormFile? image,
        CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
        {
            return (null, null);
        }

        var validationError = await ImageFileValidator.ValidateAsync(
            image,
            MaximumEvidenceImageBytes,
            cancellationToken);

        if (validationError is not null)
        {
            return (null, $"Ảnh minh chứng: {validationError}");
        }

        var storedPath = await _secureDocumentStorage.SaveAsync(
            image,
            $"extension-evidence-booking-{bookingId}",
            cancellationToken);

        return (storedPath, null);
    }

    private void DeleteSavedEvidenceImage(string? imagePath) =>
        _secureDocumentStorage.Delete(imagePath);

    private static string ComposeEvidence(
        string? evidenceNote,
        string? imagePath,
        string? liveLocationText)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(evidenceNote))
        {
            parts.AddRange(
                evidenceNote
                    .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0));
        }

        if (!string.IsNullOrWhiteSpace(liveLocationText))
        {
            parts.Add(liveLocationText);
        }

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            parts.Add($"Ảnh minh chứng: {imagePath}");
        }

        return string.Join(" | ", parts);
    }

    private static bool IsForceMajeure(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(ForceMajeureMarker, StringComparison.Ordinal);

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";
}
