using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Manager)]
public class AdminOperationsController : Controller
{
    private readonly ApplicationDbContext _dbContext;

    public AdminOperationsController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateHandover(
        int bookingId,
        int mileage,
        string fuelLevel,
        string? exteriorCondition,
        string? interiorCondition,
        string? accessories,
        string? notes,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["AdminError"] = "Chỉ có thể lập biên bản giao xe khi đơn đang ở trạng thái sẵn sàng giao.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        if (booking.Handover is not null)
        {
            TempData["AdminError"] = "Đơn thuê này đã có biên bản giao xe.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        if (string.IsNullOrWhiteSpace(fuelLevel))
        {
            TempData["AdminError"] = "Vui lòng ghi nhận mức nhiên liệu khi giao xe.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        var actualMileage = Math.Max(mileage, booking.Vehicle.CurrentMileage);
        booking.Handover = new VehicleHandover
        {
            BookingId = booking.BookingId,
            HandoverAt = DateTime.UtcNow,
            Mileage = actualMileage,
            FuelLevel = fuelLevel.Trim(),
            ExteriorCondition = string.IsNullOrWhiteSpace(exteriorCondition) ? null : exteriorCondition.Trim(),
            InteriorCondition = string.IsNullOrWhiteSpace(interiorCondition) ? null : interiorCondition.Trim(),
            Accessories = string.IsNullOrWhiteSpace(accessories) ? null : accessories.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };

        booking.Status = BookingStatus.Rented;
        booking.Vehicle.Status = VehicleStatus.Rented;
        booking.Vehicle.CurrentMileage = actualMileage;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "SmartCar đã xác nhận bàn giao xe",
            Message = $"Bạn đã nhận xe {booking.Vehicle.VehicleName}. Hãy liên hệ SmartCar ngay khi cần hỗ trợ trong chuyến đi.",
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã lập biên bản giao xe và chuyển đơn sang trạng thái đang thuê.";
        return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateVehicleReturn(
        int bookingId,
        int mileage,
        string fuelLevel,
        string? exteriorCondition,
        string? interiorCondition,
        bool hasDamage,
        string? notes,
        string? additionalChargeDescription,
        decimal additionalChargeAmount,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented)
        {
            TempData["AdminError"] = "Chỉ có thể lập biên bản trả xe khi đơn đang ở trạng thái đang thuê.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        if (booking.VehicleReturn is not null)
        {
            TempData["AdminError"] = "Đơn thuê này đã có biên bản trả xe.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        if (string.IsNullOrWhiteSpace(fuelLevel))
        {
            TempData["AdminError"] = "Vui lòng ghi nhận mức nhiên liệu khi nhận lại xe.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        var minimumMileage = booking.Handover?.Mileage ?? booking.Vehicle.CurrentMileage;
        if (mileage < minimumMileage)
        {
            TempData["AdminError"] = "Kilomet khi trả xe không được nhỏ hơn kilomet lúc giao xe.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        var vehicleReturn = new VehicleReturn
        {
            BookingId = booking.BookingId,
            ReturnedAt = DateTime.UtcNow,
            Mileage = mileage,
            FuelLevel = fuelLevel.Trim(),
            ExteriorCondition = string.IsNullOrWhiteSpace(exteriorCondition) ? null : exteriorCondition.Trim(),
            InteriorCondition = string.IsNullOrWhiteSpace(interiorCondition) ? null : interiorCondition.Trim(),
            HasDamage = hasDamage,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };

        if (additionalChargeAmount > 0)
        {
            if (string.IsNullOrWhiteSpace(additionalChargeDescription))
            {
                TempData["AdminError"] = "Vui lòng nhập nội dung phụ phí khi số tiền phụ phí lớn hơn 0.";
                return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
            }

            vehicleReturn.AdditionalCharges.Add(new AdditionalCharge
            {
                Description = additionalChargeDescription.Trim(),
                Amount = additionalChargeAmount
            });
            booking.AdditionalAmount += additionalChargeAmount;
            booking.TotalAmount += additionalChargeAmount;
        }

        booking.VehicleReturn = vehicleReturn;
        booking.Status = BookingStatus.PendingInspection;
        booking.Vehicle.CurrentMileage = mileage;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "SmartCar đã tiếp nhận xe sau chuyến",
            Message = additionalChargeAmount > 0
                ? $"SmartCar đang kiểm tra xe. Phụ phí tạm ghi nhận: {additionalChargeAmount:N0}đ."
                : "SmartCar đang kiểm tra tình trạng xe và sẽ sớm hoàn tất đơn thuê.",
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã lập biên bản trả xe và chuyển đơn sang bước kiểm tra sau chuyến.";
        return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteInspection(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.PendingInspection || booking.VehicleReturn is null)
        {
            TempData["AdminError"] = "Cần có biên bản trả xe trước khi hoàn tất kiểm tra.";
            return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
        }

        booking.Status = BookingStatus.Completed;
        booking.Vehicle.Status = VehicleStatus.Available;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đơn thuê đã hoàn thành",
            Message = $"Đơn #{booking.BookingId} đã hoàn thành. Cảm ơn bạn đã sử dụng SmartCar.",
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["AdminSuccess"] = "Đã hoàn tất kiểm tra, đóng đơn và mở lại xe cho thuê.";
        return RedirectToAction("BookingDetails", "Dashboard", new { id = bookingId });
    }
}
