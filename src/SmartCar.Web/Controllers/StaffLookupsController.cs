using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffLookupsController : Controller
{
    private const int MaximumResults = 15;

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
        CancellationToken cancellationToken)
    {
        if (pickupDate < DateTime.Now.AddMinutes(5) || pickupDate >= returnDate)
        {
            return BadRequest(new
            {
                message = "Hãy chọn thời gian nhận/trả hợp lệ trước khi tìm xe."
            });
        }

        var vehicles = await _vehicleService.SearchAvailableAsync(
            new VehicleSearchRequest(
                pickupDate,
                returnDate,
                PickupMethod: VehiclePickupMethod.StorePickup),
            cancellationToken);

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

        var result = vehicles
            .Take(MaximumResults)
            .Select(vehicle => new
            {
                value = vehicle.VehicleId,
                label = vehicle.VehicleName,
                detail = $"{vehicle.LicensePlate} · {vehicle.BrandName} · {vehicle.DailyPrice:N0} đ/ngày",
                image = vehicle.PrimaryImagePath
            })
            .ToList();

        return Json(result);
    }
}
