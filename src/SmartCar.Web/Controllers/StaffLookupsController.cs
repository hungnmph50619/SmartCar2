using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffLookupsController : Controller
{
    private const int MaximumResults = 15;
    private const int MaximumVehicleResults = 60;

    private readonly ApplicationDbContext _dbContext;
    private readonly IVehicleService _vehicleService;

    public StaffLookupsController(
        ApplicationDbContext dbContext,
        IVehicleService vehicleService)
    {
        _dbContext = dbContext;
        _vehicleService = vehicleService;
    }

    [HttpGet]
    public async Task<IActionResult> Customers(
        string? term,
        CancellationToken cancellationToken)
    {
        term = term?.Trim();
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
        {
            return Json(Array.Empty<object>());
        }

        var customerRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Customer)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(customerRoleId))
        {
            return Json(Array.Empty<object>());
        }

        var normalized = term.ToLower();
        var customers = await _dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.IsActive &&
                _dbContext.UserRoles.Any(userRole =>
                    userRole.UserId == user.Id &&
                    userRole.RoleId == customerRoleId) &&
                (user.FullName.ToLower().Contains(normalized) ||
                 (user.Email != null && user.Email.ToLower().Contains(normalized)) ||
                 (user.PhoneNumber != null && user.PhoneNumber.Contains(term))))
            .OrderBy(user => user.FullName)
            .Take(MaximumResults)
            .Select(user => new
            {
                value = user.Id,
                label = user.FullName,
                detail = (user.PhoneNumber ?? "Chưa có SĐT") + " · " + (user.Email ?? "Chưa có email")
            })
            .ToListAsync(cancellationToken);

        return Json(customers);
    }

    [HttpGet]
    public async Task<IActionResult> Vehicles(
        DateTime pickupDate,
        DateTime returnDate,
        string? term,
        CancellationToken cancellationToken,
        bool immediatePickup = false)
    {
        var policy = await SmartCar.Infrastructure.Services.BusinessPolicyStore.ReadAsync(
            _dbContext, cancellationToken);
        if (immediatePickup)
        {
            pickupDate = DateTime.Now;
        }
        if ((!immediatePickup && pickupDate < DateTime.Now.AddMinutes(policy.MinimumPickupLeadMinutes)) ||
            pickupDate >= returnDate)
        {
            return BadRequest(new
            {
                message = "Hãy chọn thời gian nhận/trả hợp lệ trước khi tìm xe."
            });
        }

        var vehicles = await _vehicleService.SearchAvailableAsync(
            new VehicleSearchRequest(
                pickupDate,
                pickupDate.AddMinutes(1),
                PickupMethod: VehiclePickupMethod.StorePickup),
            cancellationToken);

        var availableForRequestedRange = await _vehicleService.SearchAvailableAsync(
            new VehicleSearchRequest(
                pickupDate,
                returnDate,
                PickupMethod: VehiclePickupMethod.StorePickup),
            cancellationToken);
        var availableIds = availableForRequestedRange.Select(vehicle => vehicle.VehicleId).ToHashSet();

        var keyword = term?.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            vehicles = vehicles
                .Where(vehicle =>
                    vehicle.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    vehicle.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    vehicle.BrandName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(vehicle.Model) &&
                     vehicle.Model.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var candidates = vehicles.ToList();
        var candidateIds = candidates.Select(vehicle => vehicle.VehicleId).ToArray();
        var upcoming = await _dbContext.Bookings.AsNoTracking()
            .Where(booking => candidateIds.Contains(booking.VehicleId) &&
                booking.PickupDate > pickupDate &&
                (booking.Status == BookingStatus.PendingConfirmation ||
                 booking.Status == BookingStatus.PendingPayment ||
                 booking.Status == BookingStatus.Paid ||
                 booking.Status == BookingStatus.ReadyForPickup ||
                 booking.Status == BookingStatus.Rented ||
                 booking.Status == BookingStatus.PendingInspection))
            .Select(booking => new
            {
                booking.VehicleId, booking.PickupDate, booking.PickupMethod, booking.PolicyJson
            })
            .ToListAsync(cancellationToken);
        var nextByVehicle = upcoming
            .GroupBy(booking => booking.VehicleId)
            .ToDictionary(group => group.Key, group => group.OrderBy(booking => booking.PickupDate).First());

        var result = candidates
            .Select(vehicle =>
            {
                nextByVehicle.TryGetValue(vehicle.VehicleId, out var next);
                DateTime? latestReturn = next is null ? null : CounterRentalSchedule.LatestReturn(
                    next.PickupDate,
                    next.PickupMethod,
                    RentalPolicySnapshot.FromJson(next.PolicyJson));
                var canBook = availableIds.Contains(vehicle.VehicleId);
                return new
                {
                    vehicle,
                    canBook,
                    latestReturn,
                    nextPickup = next?.PickupDate
                };
            })
            .Where(item => item.canBook ||
                (item.latestReturn.HasValue && item.latestReturn.Value > pickupDate &&
                 item.latestReturn.Value < returnDate))
            .OrderByDescending(item => item.canBook)
            .Take(MaximumVehicleResults)
            .Select(item => new
            {
                value = item.vehicle.VehicleId,
                label = item.vehicle.VehicleName,
                detail = item.canBook
                    ? $"{item.vehicle.LicensePlate} · {item.vehicle.BrandName} · {item.vehicle.DailyPrice:N0} đ/ngày" +
                      (item.latestReturn.HasValue
                          ? $" · Có đơn sau: nhận {item.nextPickup:dd/MM HH:mm}, trả muộn nhất {item.latestReturn:dd/MM HH:mm}"
                          : string.Empty)
                    : $"{item.vehicle.LicensePlate} · Giờ trả muộn nhất {item.latestReturn:dd/MM HH:mm} " +
                      $"để chuẩn bị cho đơn nhận {item.nextPickup:dd/MM HH:mm}",
                image = item.vehicle.PrimaryImagePath,
                canBook = item.canBook,
                latestReturn = item.latestReturn?.ToString("yyyy-MM-ddTHH:mm")
            })
            .ToList();

        return Json(result);
    }
}
