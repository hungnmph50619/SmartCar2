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
    private const string CompensationMarker = "[NEXT_BOOKING_COMPENSATION]";

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
    private readonly IWebHostEnvironment _environment;

    public AdminExtensionsController(
        IExtensionService extensionService,
        IBookingService bookingService,
        IBookingOperationService bookingOperationService,
        IAuditService auditService,
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment)
    {
        _extensionService = extensionService;
        _bookingService = bookingService;
        _bookingOperationService = bookingOperationService;
        _auditService = auditService;
        _dbContext = dbContext;
        _environment = environment;
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
                TempData["ErrorMessage"] = "Vui lòng ghi rõ lý do bất khả kháng khách trình bày qua điện thoại.";
                return RedirectToAction(nameof(Index));
            }

            if (evidenceImage is null || evidenceImage.Length == 0)
            {
                TempData["ErrorMessage"] = "Yêu cầu bất khả kháng qua điện thoại phải có ảnh minh chứng khách gửi.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(customerLiveLocation))
            {
                TempData["ErrorMessage"] = "Vui lòng ghi vị trí trực tiếp khách đã gửi/cung cấp cho SmartCar.";
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
            ? isForceMajeure
                ? "Đã ghi nhận yêu cầu bất khả kháng qua điện thoại kèm ảnh và vị trí khách cung cấp."
                : "Đã ghi nhận yêu cầu gia hạn qua điện thoại."
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
            TempData["ErrorMessage"] = "Chỉ đổi xe sau khi khách của đơn kế tiếp đã đồng ý phương án xe và mức giá mới.";
            return RedirectToAction(nameof(Index));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

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
            TempData["ErrorMessage"] = "Đơn kế tiếp không còn xung đột; hãy tải lại danh sách và tiếp tục duyệt gia hạn.";
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

        var replacementHasConflict = await _dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(item =>
                item.VehicleId == replacement.VehicleId &&
                item.BookingId != conflict.BookingId &&
                BlockingStatuses.Contains(item.Status) &&
                item.PickupDate < conflict.ReturnDate &&
                item.ReturnDate > conflict.PickupDate,
                cancellationToken);

        if (replacementHasConflict)
        {
            TempData["ErrorMessage"] = "Xe thay thế vừa phát sinh lịch thuê trùng. Vui lòng chọn xe khác.";
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
        var newRentalAmount = conflict.NumberOfDays * replacement.DailyPrice;
        var newDepositAmount = RentalPolicy.CalculateDeposit(newRentalAmount);
        var newRentalPaymentAmount = newRentalAmount + deliveryFee;

        var rentalPayment = conflict.Payments.FirstOrDefault(item => item.Type == PaymentType.Rental);
        var depositPayment = conflict.Payments.FirstOrDefault(item => item.Type == PaymentType.Deposit);

        if ((rentalPayment?.Status == PaymentStatus.AwaitingConfirmation && rentalPayment.Amount != newRentalPaymentAmount) ||
            (depositPayment?.Status == PaymentStatus.AwaitingConfirmation && depositPayment.Amount != newDepositAmount))
        {
            TempData["ErrorMessage"] = "Đơn kế tiếp đang chờ đối soát số tiền cũ. Hãy xử lý giao dịch này trước hoặc chọn xe có giá tương đương.";
            return RedirectToAction(nameof(Index));
        }

        decimal paidPriceDifference = 0m;
        paidPriceDifference += AdjustPaymentAmount(rentalPayment, newRentalPaymentAmount);
        paidPriceDifference += AdjustPaymentAmount(depositPayment, newDepositAmount);

        var oldVehicleName = conflict.Vehicle.VehicleName;
        var oldVehicleId = conflict.VehicleId;
        conflict.VehicleId = replacement.VehicleId;
        conflict.Vehicle = replacement;
        conflict.DailyPrice = replacement.DailyPrice;
        conflict.RentalAmount = newRentalAmount;
        conflict.DepositAmount = newDepositAmount;
        conflict.TotalAmount = newRentalAmount + deliveryFee + conflict.AdditionalAmount;

        if (paidPriceDifference > 0)
        {
            conflict.Payments.Add(new Payment
            {
                Type = PaymentType.AdditionalCharge,
                Amount = paidPriceDifference,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending
            });
        }
        else if (paidPriceDifference < 0)
        {
            var refundAmount = Math.Abs(paidPriceDifference);
            conflict.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = refundAmount,
                Method = PaymentMethods.BankTransferRefund,
                Status = PaymentStatus.AwaitingRefund
            });
            conflict.RefundAmount += refundAmount;
            conflict.RefundReason = AppendText(
                conflict.RefundReason,
                $"Đổi xe theo phương án xử lý gia hạn bất khả kháng; hoàn chênh lệch {refundAmount:N0} đồng.");
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = conflict.CustomerId,
            Title = "SmartCar đề xuất và ghi nhận đổi xe",
            Message = paidPriceDifference switch
            {
                > 0 => $"Đơn #{conflict.BookingId} đã được chuyển từ {oldVehicleName} sang {replacement.VehicleName} ({replacement.LicensePlate}) theo phương án bạn đã đồng ý. Chênh lệch cần thanh toán thêm: {paidPriceDifference:N0} đồng.",
                < 0 => $"Đơn #{conflict.BookingId} đã được chuyển từ {oldVehicleName} sang {replacement.VehicleName} ({replacement.LicensePlate}) theo phương án bạn đã đồng ý. Chênh lệch được hoàn: {Math.Abs(paidPriceDifference):N0} đồng.",
                _ => $"Đơn #{conflict.BookingId} đã được chuyển từ {oldVehicleName} sang {replacement.VehicleName} ({replacement.LicensePlate}) với mức giá tương đương."
            }
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "ResolveExtensionConflictByVehicleSwap",
            nameof(Booking),
            conflict.BookingId.ToString(),
            $"Đổi xe đơn #{conflict.BookingId} từ xe #{oldVehicleId} sang xe #{replacement.VehicleId} để xử lý xung đột gia hạn bất khả kháng của đơn #{extension.BookingId}. Chênh lệch đã thanh toán: {paidPriceDifference:N0} đồng.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã chuyển đơn kế tiếp sang xe khách đã đồng ý. Xung đột lịch xe đã được xử lý.";
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
            TempData["ErrorMessage"] = "Vui lòng xác nhận đã liên hệ khách của đơn kế tiếp và khách không chấp nhận phương án đổi xe.";
            return RedirectToAction(nameof(Index));
        }

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
            TempData["ErrorMessage"] = "Đơn kế tiếp không còn xung đột.";
            return RedirectToAction(nameof(Index));
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var conflictBookingId = conflict.BookingId;
        var conflictContractAmount = conflict.TotalAmount;
        var currentRenterDepositPaid = extension.Booking.Payments
            .Where(item => item.Type == PaymentType.Deposit && item.Status == PaymentStatus.Paid)
            .Sum(item => item.Amount);
        var compensationAmount = Math.Min(conflictContractAmount, currentRenterDepositPaid);

        var cancelResult = await _bookingOperationService.CancelByAdminAsync(
            adminId,
            new CancelBookingRequest(
                conflictBookingId,
                $"SmartCar phải hủy do đơn #{extension.BookingId} phát sinh gia hạn bất khả kháng và khách không chấp nhận phương án đổi xe."),
            cancellationToken);

        if (!cancelResult.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", cancelResult.Errors);
            return RedirectToAction(nameof(Index));
        }

        extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
            .FirstAsync(item => item.BookingExtensionId == extensionId, cancellationToken);
        var cancelledBooking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstAsync(item => item.BookingId == conflictBookingId, cancellationToken);

        if (compensationAmount > 0)
        {
            cancelledBooking.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = compensationAmount,
                Method = PaymentMethods.BankTransferRefund,
                Status = PaymentStatus.AwaitingRefund
            });
            cancelledBooking.RefundAmount += compensationAmount;
            cancelledBooking.RefundReason = AppendText(
                cancelledBooking.RefundReason,
                $"Hỗ trợ/bồi thường do SmartCar không thể thực hiện đơn sau xử lý gia hạn bất khả kháng: {compensationAmount:N0} đồng.");

            extension.CustomerNote = AppendCompensationMarker(
                extension.CustomerNote,
                compensationAmount,
                conflictBookingId);

            _dbContext.Notifications.Add(new Notification
            {
                UserId = cancelledBooking.CustomerId,
                Title = "Hoàn tiền và hỗ trợ do thay đổi lịch xe",
                Message = $"Đơn #{conflictBookingId} đã được SmartCar hủy sau khi không thống nhất được phương án đổi xe. Ngoài khoản hoàn của đơn, SmartCar ghi nhận thêm khoản hỗ trợ {compensationAmount:N0} đồng đang chờ chuyển."
            });

            _dbContext.Notifications.Add(new Notification
            {
                UserId = extension.Booking.CustomerId,
                Title = "Ghi nhận bồi thường đơn thuê kế tiếp",
                Message = $"Do gia hạn bất khả kháng của đơn #{extension.BookingId} làm hủy đơn #{conflictBookingId}, SmartCar ghi nhận khoản bồi thường {compensationAmount:N0} đồng để đối soát/khấu trừ từ tiền cọc khi trả xe."
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.WriteAsync(
            adminId,
            "ResolveExtensionConflictByCancellation",
            nameof(Booking),
            conflictBookingId.ToString(),
            $"Hủy đơn #{conflictBookingId} do xung đột gia hạn bất khả kháng của đơn #{extension.BookingId}. Hoàn theo chính sách: {cancelResult.RefundAmount:N0} đồng; hỗ trợ bổ sung: {compensationAmount:N0} đồng.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = compensationAmount > 0
            ? $"Đã hủy đơn kế tiếp, tạo hoàn tiền và khoản hỗ trợ {compensationAmount:N0} đồng. Khoản này đã được ghi nhận để đối soát từ cọc của khách đang thuê."
            : "Đã hủy đơn kế tiếp và tạo khoản hoàn theo chính sách. Xung đột lịch xe đã được xử lý.";

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
            ? "Đã duyệt gia hạn và tạo khoản thanh toán. Ngày trả mới chỉ có hiệu lực sau khi xác nhận thanh toán."
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
            ? "Đã từ chối yêu cầu gia hạn và gửi lý do cho khách."
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
            .Include(item => item.Payments)
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

        var depositPaidByCurrentRenter = await _dbContext.Payments
            .AsNoTracking()
            .Where(item =>
                item.BookingId == extension.BookingId &&
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.Paid)
            .SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;

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
            ContractAmount = conflict.TotalAmount,
            DepositPaidByCurrentRenter = depositPaidByCurrentRenter,
            Alternatives = alternatives
        };
    }

    private async Task<IReadOnlyList<ExtensionAlternativeVehicleViewModel>> GetAlternativeVehiclesAsync(
        Booking conflict,
        CancellationToken cancellationToken)
    {
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
                    booking.PickupDate < conflict.ReturnDate &&
                    booking.ReturnDate > conflict.PickupDate) &&
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
        return await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .Where(other =>
                other.VehicleId == extension.Booking.VehicleId &&
                other.BookingId != extension.BookingId &&
                BlockingStatuses.Contains(other.Status) &&
                other.PickupDate < extension.RequestedReturnDate &&
                other.ReturnDate > extension.OriginalReturnDate)
            .OrderBy(other => other.PickupDate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static decimal AdjustPaymentAmount(Payment? payment, decimal newAmount)
    {
        if (payment is null)
        {
            return 0m;
        }

        if (payment.Status == PaymentStatus.Paid)
        {
            return newAmount - payment.Amount;
        }

        if (payment.Status == PaymentStatus.Pending)
        {
            payment.Amount = newAmount;
        }

        return 0m;
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

        var relativeFolder = $"uploads/extensions/{bookingId}";
        var physicalFolder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(physicalFolder);

        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var physicalPath = Path.Combine(physicalFolder, fileName);

        await using var stream = new FileStream(
            physicalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        await image.CopyToAsync(stream, cancellationToken);

        return ($"/{relativeFolder}/{fileName}", null);
    }

    private void DeleteSavedEvidenceImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) ||
            !imagePath.StartsWith("/uploads/extensions/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relative = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var physicalPath = Path.Combine(_environment.WebRootPath, relative);
        if (System.IO.File.Exists(physicalPath))
        {
            System.IO.File.Delete(physicalPath);
        }
    }

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

    private static string AppendCompensationMarker(
        string? customerNote,
        decimal amount,
        int affectedBookingId)
    {
        var marker = $"{CompensationMarker}{amount:0.##}|BOOKING:{affectedBookingId}";
        return string.IsNullOrWhiteSpace(customerNote)
            ? marker
            : $"{customerNote.Trim()}\n{marker}";
    }

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";
}
