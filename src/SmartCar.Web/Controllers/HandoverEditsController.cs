using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class HandoverEditsController : Controller
{
    private const string SignedMarker = "signed-handover-";
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuditService _auditService;

    public HandoverEditsController(ApplicationDbContext dbContext, IAuditService auditService)
    {
        _dbContext = dbContext;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int bookingId, CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe.";
            return RedirectToAction("Details", "AdminBookings", new { id = bookingId });
        }

        if (!CanEdit(booking.Status, booking.Handover.ImagePaths))
        {
            TempData["ErrorMessage"] = "Biên bản đã được ký hoặc chuyến đã bắt đầu nên không thể chỉnh sửa.";
            return RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId });
        }

        var record = booking.Handover;
        return View(new HandoverViewModel
        {
            BookingId = bookingId,
            HandoverAt = record.HandoverAt,
            Mileage = record.Mileage,
            FuelLevel = record.FuelLevel.TrimEnd('%').Trim(),
            ExteriorCondition = record.ExteriorCondition,
            InteriorCondition = record.InteriorCondition,
            Accessories = record.Accessories,
            IncludedKilometers = record.IncludedKilometers,
            ExcessKmFeePerKm = record.ExcessKmFeePerKm,
            LateReturnFeeMultiplier = record.LateReturnFeeMultiplier,
            TrafficFineTerms = record.TrafficFineTerms,
            DamageCompensationTerms = record.DamageCompensationTerms,
            PenaltyPolicyAccepted = true,
            Notes = record.Notes
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(HandoverViewModel model, CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(HandoverViewModel.Images));
        ModelState.Remove(nameof(HandoverViewModel.FrontImage));
        ModelState.Remove(nameof(HandoverViewModel.RearImage));
        ModelState.Remove(nameof(HandoverViewModel.LeftImage));
        ModelState.Remove(nameof(HandoverViewModel.RightImage));
        ModelState.Remove(nameof(HandoverViewModel.InteriorImage));
        ModelState.Remove(nameof(HandoverViewModel.OdometerImage));
        ModelState.Remove(nameof(HandoverViewModel.FuelImage));
        ModelState.Remove(nameof(HandoverViewModel.IncludedKilometers));
        ModelState.Remove(nameof(HandoverViewModel.ExcessKmFeePerKm));
        ModelState.Remove(nameof(HandoverViewModel.LateReturnFeeMultiplier));
        ModelState.Remove(nameof(HandoverViewModel.TrafficFineTerms));
        ModelState.Remove(nameof(HandoverViewModel.DamageCompensationTerms));
        ModelState.Remove(nameof(HandoverViewModel.PenaltyPolicyAccepted));

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == model.BookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            return NotFound();
        }

        if (!CanEdit(booking.Status, booking.Handover.ImagePaths))
        {
            TempData["ErrorMessage"] = "Biên bản đã được ký hoặc chuyến đã bắt đầu nên không thể chỉnh sửa.";
            return RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId = model.BookingId });
        }

        if (model.HandoverAt < booking.PickupDate || model.HandoverAt >= booking.ReturnDate)
        {
            ModelState.AddModelError(nameof(model.HandoverAt), "Thời gian giao phải nằm trong khoảng thuê đã đặt.");
        }

        if (!model.Mileage.HasValue || model.Mileage.Value < booking.Vehicle.CurrentMileage)
        {
            ModelState.AddModelError(nameof(model.Mileage), $"Số km không được nhỏ hơn số km hiện tại ({booking.Vehicle.CurrentMileage:N0} km).");
        }

        if (!TryParseFuel(model.FuelLevel, out var fuelPercent))
        {
            ModelState.AddModelError(nameof(model.FuelLevel), "Mức nhiên liệu phải từ 0 đến 100%.");
        }

        if (!ModelState.IsValid)
        {
            var record = booking.Handover;
            model.IncludedKilometers = record.IncludedKilometers;
            model.ExcessKmFeePerKm = record.ExcessKmFeePerKm;
            model.LateReturnFeeMultiplier = record.LateReturnFeeMultiplier;
            model.TrafficFineTerms = record.TrafficFineTerms;
            model.DamageCompensationTerms = record.DamageCompensationTerms;
            model.PenaltyPolicyAccepted = true;
            return View(model);
        }

        booking.Handover.HandoverAt = model.HandoverAt;
        booking.Handover.Mileage = model.Mileage!.Value;
        booking.Handover.FuelLevel = $"{fuelPercent}%";
        booking.Handover.ExteriorCondition = Normalize(model.ExteriorCondition);
        booking.Handover.InteriorCondition = Normalize(model.InteriorCondition);
        booking.Handover.Accessories = Normalize(model.Accessories);
        booking.Handover.Notes = Normalize(model.Notes);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "EditHandover",
            nameof(VehicleHandover),
            model.BookingId.ToString(),
            $"Chỉnh sửa biên bản giao xe chưa ký của đơn #{model.BookingId}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã cập nhật biên bản giao xe. Hãy kiểm tra lại trước khi in và ký.";
        return RedirectToAction("HandoverPrint", "AdminRentalDocuments", new { bookingId = model.BookingId });
    }

    private static bool CanEdit(BookingStatus status, string? imagePaths) =>
        status == BookingStatus.ReadyForPickup &&
        !SplitPaths(imagePaths).Any(path => path.Contains(SignedMarker, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitPaths(string? imagePaths) =>
        string.IsNullOrWhiteSpace(imagePaths)
            ? Array.Empty<string>()
            : imagePaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseFuel(string? value, out int percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().TrimEnd('%').Trim();
        return int.TryParse(normalized, out percent) && percent is >= 0 and <= 100;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
