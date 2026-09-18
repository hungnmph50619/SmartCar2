using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Extensions;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffExtensionOperationsController : Controller
{
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
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;

    public StaffExtensionOperationsController(
        IExtensionService extensionService,
        IAuditService auditService,
        ApplicationDbContext dbContext)
    {
        _extensionService = extensionService;
        _auditService = auditService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var extensions = await _extensionService.GetPendingExtensionsAsync(cancellationToken);
        var operationalConflicts = extensions
            .Where(item =>
                item.Status == BookingExtensionStatus.Pending &&
                item.IsForceMajeure &&
                item.HasScheduleConflict &&
                item.ConflictingBookingId.HasValue)
            .ToList();

        var conflictResolutions =
            new Dictionary<int, ExtensionConflictResolutionViewModel>();

        foreach (var item in operationalConflicts)
        {
            var resolution = await BuildConflictResolutionAsync(
                item,
                cancellationToken);
            if (resolution is not null)
            {
                conflictResolutions[item.BookingExtensionId] = resolution;
            }
        }

        ViewBag.ConflictResolutions = conflictResolutions;
        return View(operationalConflicts);
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
            TempData["ErrorMessage"] =
                "Chỉ đổi xe sau khi nhân viên đã liên hệ và khách có đơn kế tiếp đồng ý xe thay thế cùng phần chênh lệch giá.";
            return RedirectToAction(nameof(Index));
        }

        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        var extension = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .FirstOrDefaultAsync(
                item => item.BookingExtensionId == extensionId,
                cancellationToken);

        if (extension is null ||
            extension.Status != BookingExtensionStatus.Pending ||
            !IsForceMajeure(extension.CustomerNote))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Yêu cầu gia hạn không còn hợp lệ để nhân viên xử lý xung đột.";
            return RedirectToAction(nameof(Index));
        }

        var conflict = await FindConflictingBookingAsync(
            extension,
            cancellationToken);
        if (conflict is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Đơn kế tiếp không còn xung đột. Hãy tải lại danh sách trước khi thao tác.";
            return RedirectToAction(nameof(Index));
        }

        var replacement = await _dbContext.Vehicles
            .FirstOrDefaultAsync(
                item => item.VehicleId == replacementVehicleId,
                cancellationToken);

        if (replacement is null ||
            replacement.VehicleId == conflict.VehicleId ||
            replacement.Status is VehicleStatus.Maintenance
                or VehicleStatus.Inspection
                or VehicleStatus.Inactive)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Xe thay thế không còn khả dụng.";
            return RedirectToAction(nameof(Index));
        }

        var conflictPreparation = TimeSpan.FromMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(
                conflict.PickupMethod));
        var conflictPickupBoundary =
            conflict.PickupDate - conflictPreparation;
        var conflictReturnWithStorePreparation =
            conflict.ReturnDate.AddMinutes(
                RentalPolicy.GetOperationalPreparationMinutes(
                    VehiclePickupMethod.StorePickup));
        var conflictReturnWithDeliveryPreparation =
            conflict.ReturnDate.AddMinutes(
                RentalPolicy.GetOperationalPreparationMinutes(
                    VehiclePickupMethod.Delivery));

        var replacementHasConflict = await _dbContext.Bookings
            .AsNoTracking()
            .AnyAsync(item =>
                item.VehicleId == replacement.VehicleId &&
                item.BookingId != conflict.BookingId &&
                BlockingStatuses.Contains(item.Status) &&
                conflictPickupBoundary < item.ReturnDate &&
                (
                    item.PickupMethod == VehiclePickupMethod.Delivery
                        ? conflictReturnWithDeliveryPreparation >
                          item.PickupDate
                        : conflictReturnWithStorePreparation >
                          item.PickupDate
                ),
                cancellationToken);

        if (replacementHasConflict)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Xe thay thế vừa phát sinh lịch xung đột. Vui lòng chọn xe khác.";
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
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Xe thay thế đang có sự cố chưa xử lý.";
            return RedirectToAction(nameof(Index));
        }

        var deliveryFee = Math.Max(
            0m,
            conflict.TotalAmount -
            conflict.RentalAmount -
            conflict.AdditionalAmount);
        var oldRentalAmount = conflict.RentalAmount;
        var oldDepositAmount = conflict.DepositAmount;
        var newRentalAmount =
            conflict.NumberOfDays * replacement.DailyPrice;
        var newDepositAmount =
            RentalPolicy.CalculateDeposit(newRentalAmount);
        var newRentalPaymentAmount =
            newRentalAmount + deliveryFee;
        var overallDifference =
            (newRentalAmount + newDepositAmount) -
            (oldRentalAmount + oldDepositAmount);

        var hasSettlementAwaitingConfirmation =
            conflict.Payments.Any(item =>
                item.Status == PaymentStatus.AwaitingConfirmation &&
                item.Type is PaymentType.Rental
                    or PaymentType.Deposit
                    or PaymentType.VehicleSwapAdjustment);

        if (hasSettlementAwaitingConfirmation)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Đơn kế tiếp đang có giao dịch trước giao xe chờ Staff đối soát. Cần xử lý giao dịch trước khi đổi xe.";
            return RedirectToAction(nameof(Index));
        }

        FailPendingPayments(
            conflict.Payments,
            PaymentType.VehicleSwapAdjustment);

        var grossRentalPaid = conflict.Payments
            .Where(item =>
                item.Status == PaymentStatus.Paid &&
                item.Type is PaymentType.Rental
                    or PaymentType.VehicleSwapAdjustment)
            .Sum(item => item.Amount);
        var rentalRefundPlanned = conflict.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.VehicleSwapRefund &&
                item.Status is PaymentStatus.AwaitingRefund
                    or PaymentStatus.RefundApproved
                    or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveRentalPaid =
            BookingWorkflowRules.CalculateEffectivePaid(
                grossRentalPaid,
                rentalRefundPlanned);

        var grossDepositPaid = conflict.Payments
            .Where(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.Paid)
            .Sum(item => item.Amount);
        var depositRefundPlanned = conflict.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.DepositRefund &&
                item.Status is PaymentStatus.AwaitingRefund
                    or PaymentStatus.RefundApproved
                    or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveDepositPaid =
            BookingWorkflowRules.CalculateEffectivePaid(
                grossDepositPaid,
                depositRefundPlanned);

        decimal rentalPaidDifference;
        if (grossRentalPaid > 0m)
        {
            rentalPaidDifference =
                newRentalPaymentAmount - effectiveRentalPaid;
            FailPendingPayments(
                conflict.Payments,
                PaymentType.Rental);
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
            depositPaidDifference =
                newDepositAmount - effectiveDepositPaid;
            FailPendingPayments(
                conflict.Payments,
                PaymentType.Deposit);
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
        conflict.TotalAmount =
            newRentalAmount +
            deliveryFee +
            conflict.AdditionalAmount;

        var amountToCollect =
            Math.Max(0m, rentalPaidDifference) +
            Math.Max(0m, depositPaidDifference);
        var rentalRefund =
            Math.Abs(Math.Min(0m, rentalPaidDifference));
        var depositRefund =
            Math.Abs(Math.Min(0m, depositPaidDifference));
        var totalRefund = rentalRefund + depositRefund;

        if (amountToCollect > 0m)
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
                conflict.Status = BookingStatus.Paid;
            }
        }

        if (rentalRefund > 0m)
        {
            conflict.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = rentalRefund,
                Method = PaymentMethods.VehicleSwapRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        if (depositRefund > 0m)
        {
            conflict.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = depositRefund,
                Method = PaymentMethods.DepositRefund,
                Status = PaymentStatus.AwaitingRefund
            });
        }

        if (totalRefund > 0m)
        {
            conflict.RefundAmount = conflict.Payments
                .Where(payment =>
                    payment.Type == PaymentType.Refund &&
                    BookingWorkflowRules.CountsTowardRefundTotal(
                        payment.Status))
                .Sum(payment => payment.Amount);
            conflict.RefundReason = AppendText(
                conflict.RefundReason,
                $"Đổi xe: hoàn chênh lệch {totalRefund:N0} đồng.");
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = conflict.CustomerId,
            Title = "Nhân viên đã xác nhận đổi xe",
            Message = amountToCollect > 0
                ? $"Đơn #{conflict.BookingId} đã đổi từ {oldVehicleName} sang {replacement.VehicleName} ({replacement.LicensePlate}). Cần thanh toán thêm {amountToCollect:N0} đồng."
                : totalRefund > 0
                    ? $"Đơn #{conflict.BookingId} đã đổi từ {oldVehicleName} sang {replacement.VehicleName} ({replacement.LicensePlate}). SmartCar sẽ hoàn chênh lệch {totalRefund:N0} đồng sau bước duyệt hoàn."
                    : overallDifference > 0
                        ? $"Đơn #{conflict.BookingId} đã đổi sang {replacement.VehicleName} ({replacement.LicensePlate}). Số phải thanh toán tăng {overallDifference:N0} đồng."
                        : overallDifference < 0
                            ? $"Đơn #{conflict.BookingId} đã đổi sang {replacement.VehicleName} ({replacement.LicensePlate}). Số phải thanh toán giảm {Math.Abs(overallDifference):N0} đồng."
                            : $"Đơn #{conflict.BookingId} đã đổi sang {replacement.VehicleName} ({replacement.LicensePlate}), không phát sinh chênh lệch."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var staffId =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            string.Empty;
        await _auditService.WriteAsync(
            staffId,
            "StaffResolveExtensionConflictByVehicleSwap",
            nameof(Booking),
            conflict.BookingId.ToString(),
            $"Nhân viên đổi xe đơn #{conflict.BookingId} từ xe #{oldVehicleId} sang xe #{replacement.VehicleId}. Thu thêm: {amountToCollect:N0}; hoàn chờ duyệt: {totalRefund:N0} đồng.",
            ipAddress:
                HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = amountToCollect > 0
            ? $"Đã đổi xe cho đơn #{conflict.BookingId}. Cần thu thêm {amountToCollect:N0} đ."
            : totalRefund > 0
                ? $"Đã đổi xe cho đơn #{conflict.BookingId}. Có {totalRefund:N0} đ chờ Admin duyệt hoàn."
                : $"Đã đổi xe cho đơn #{conflict.BookingId}. Không phát sinh chênh lệch.";

        return RedirectToAction(nameof(Index));
    }

    private async Task<ExtensionConflictResolutionViewModel?>
        BuildConflictResolutionAsync(
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
            .FirstOrDefaultAsync(
                item =>
                    item.BookingId ==
                    extension.ConflictingBookingId.Value,
                cancellationToken);

        if (conflict is null)
        {
            return null;
        }

        var customerName = await _dbContext.Users
            .AsNoTracking()
            .Where(item => item.Id == conflict.CustomerId)
            .Select(item => item.FullName)
            .FirstOrDefaultAsync(cancellationToken)
            ?? "Khách hàng";

        var alternatives = await GetAlternativeVehiclesAsync(
            conflict,
            cancellationToken);

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

    private async Task<IReadOnlyList<ExtensionAlternativeVehicleViewModel>>
        GetAlternativeVehiclesAsync(
            Booking conflict,
            CancellationToken cancellationToken)
    {
        var conflictPreparation = TimeSpan.FromMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(
                conflict.PickupMethod));
        var conflictPickupBoundary =
            conflict.PickupDate - conflictPreparation;
        var conflictReturnWithStorePreparation =
            conflict.ReturnDate.AddMinutes(
                RentalPolicy.GetOperationalPreparationMinutes(
                    VehiclePickupMethod.StorePickup));
        var conflictReturnWithDeliveryPreparation =
            conflict.ReturnDate.AddMinutes(
                RentalPolicy.GetOperationalPreparationMinutes(
                    VehiclePickupMethod.Delivery));

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
                        booking.PickupMethod ==
                        VehiclePickupMethod.Delivery
                            ? conflictReturnWithDeliveryPreparation >
                              booking.PickupDate
                            : conflictReturnWithStorePreparation >
                              booking.PickupDate
                    )) &&
                !_dbContext.VehicleIncidents.Any(incident =>
                    incident.VehicleId == vehicle.VehicleId &&
                    incident.Status != IncidentStatus.Resolved &&
                    incident.Status != IncidentStatus.Cancelled))
            .Select(vehicle =>
                new ExtensionAlternativeVehicleViewModel
                {
                    VehicleId = vehicle.VehicleId,
                    VehicleName = vehicle.VehicleName,
                    LicensePlate = vehicle.LicensePlate,
                    DailyPrice = vehicle.DailyPrice,
                    EstimatedRentalAmount =
                        conflict.NumberOfDays *
                        vehicle.DailyPrice,
                    CurrentRentalAmount =
                        conflict.RentalAmount
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
        var storeBoundary =
            extension.RequestedReturnDate.AddMinutes(
                RentalPolicy.GetOperationalPreparationMinutes(
                    VehiclePickupMethod.StorePickup));
        var deliveryBoundary =
            extension.RequestedReturnDate.AddMinutes(
                RentalPolicy.GetOperationalPreparationMinutes(
                    VehiclePickupMethod.Delivery));

        return await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .Where(other =>
                other.VehicleId == extension.Booking.VehicleId &&
                other.BookingId != extension.BookingId &&
                BlockingStatuses.Contains(other.Status) &&
                other.ReturnDate > extension.OriginalReturnDate &&
                (
                    other.PickupMethod ==
                    VehiclePickupMethod.Delivery
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
            .Where(item =>
                item.Type == type &&
                item.Status == PaymentStatus.Pending)
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

    private static bool IsForceMajeure(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(
            ForceMajeureMarker,
            StringComparison.Ordinal);

    private static string AppendText(
        string? current,
        string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";
}
