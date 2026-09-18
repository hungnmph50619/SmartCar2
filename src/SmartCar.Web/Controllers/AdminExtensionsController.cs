using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Extensions;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminExtensionsController : Controller
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

    public AdminExtensionsController(
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

    private static bool IsForceMajeure(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(ForceMajeureMarker, StringComparison.Ordinal);

}
