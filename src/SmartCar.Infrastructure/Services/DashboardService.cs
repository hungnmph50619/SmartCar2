using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Dashboard;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class DashboardService : IDashboardService
{
    private readonly ApplicationDbContext _dbContext;

    public DashboardService(
        ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DashboardDto> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var tomorrow = today.AddDays(1);

        var firstDayOfMonth =
            new DateTime(
                now.Year,
                now.Month,
                1);

        var firstDayOfNextMonth =
            firstDayOfMonth.AddMonths(1);

        var requiredKycTypes = new[]
        {
            DocumentTypes.CitizenId,
            DocumentTypes.CitizenIdBack,
            DocumentTypes.DrivingLicense,
            DocumentTypes.DrivingLicenseBack
        };

        var recentBookings =
            await _dbContext.Bookings
                .AsNoTracking()
                .OrderByDescending(
                    booking => booking.CreatedAt)
                .Take(8)
                .Select(booking =>
                    new BookingListItemDto
                    {
                        BookingId =
                            booking.BookingId,

                        CustomerName =
                            _dbContext.Users
                                .Where(user =>
                                    user.Id ==
                                    booking.CustomerId)
                                .Select(user =>
                                    user.FullName)
                                .FirstOrDefault()
                            ?? string.Empty,

                        CustomerPhone =
                            _dbContext.Users
                                .Where(user =>
                                    user.Id ==
                                    booking.CustomerId)
                                .Select(user =>
                                    user.PhoneNumber)
                                .FirstOrDefault(),

                        VehicleId =
                            booking.VehicleId,

                        VehicleName =
                            booking.Vehicle.VehicleName,

                        LicensePlate =
                            booking.Vehicle.LicensePlate,

                        PrimaryImagePath =
                            booking.Vehicle.Images
                                .OrderByDescending(
                                    image =>
                                        image.IsPrimary)
                                .ThenBy(
                                    image =>
                                        image.SortOrder)
                                .Select(
                                    image =>
                                        image.ImagePath)
                                .FirstOrDefault(),

                        PickupDate =
                            booking.PickupDate,

                        ReturnDate =
                            booking.ReturnDate,

                        TotalAmount =
                            booking.TotalAmount,

                        Status =
                            booking.Status,

                        CreatedAt =
                            booking.CreatedAt
                    })
                .ToListAsync(cancellationToken);

        var pendingKycPackages =
            await _dbContext.CustomerDocuments
                .AsNoTracking()
                .Where(document =>
                    document.Status ==
                        DocumentStatus.Pending &&
                    requiredKycTypes.Contains(
                        document.DocumentType))
                .Select(document =>
                    document.CustomerId)
                .Distinct()
                .CountAsync(cancellationToken);

        var pendingExtensions =
            await _dbContext.BookingExtensions
                .AsNoTracking()
                .CountAsync(
                    extension =>
                        extension.Status ==
                        BookingExtensionStatus.Pending,
                    cancellationToken);

        var pendingQrPayments =
            await _dbContext.Payments
                .AsNoTracking()
                .CountAsync(
                    payment =>
                        payment.Status ==
                            PaymentStatus
                                .AwaitingConfirmation &&
                        payment.Method ==
                            PaymentMethods.BankQr,
                    cancellationToken);

        return new DashboardDto
        {
            TotalVehicles =
                await _dbContext.Vehicles
                    .CountAsync(
                        vehicle =>
                            vehicle.Status !=
                            VehicleStatus.Inactive,
                        cancellationToken),

            AvailableVehicles =
                await _dbContext.Vehicles
                    .CountAsync(
                        vehicle =>
                            vehicle.Status ==
                            VehicleStatus.Available,
                        cancellationToken),

            RentedVehicles =
                await _dbContext.Vehicles
                    .CountAsync(
                        vehicle =>
                            vehicle.Status ==
                            VehicleStatus.Rented,
                        cancellationToken),

            InspectionVehicles =
                await _dbContext.Vehicles
                    .CountAsync(
                        vehicle =>
                            vehicle.Status ==
                            VehicleStatus.Inspection,
                        cancellationToken),

            MaintenanceVehicles =
                await _dbContext.Vehicles
                    .CountAsync(
                        vehicle =>
                            vehicle.Status ==
                            VehicleStatus.Maintenance,
                        cancellationToken),

            PendingBookings =
                await _dbContext.Bookings
                    .CountAsync(
                        booking =>
                            booking.Status ==
                            BookingStatus
                                .PendingConfirmation,
                        cancellationToken),

            PendingKycPackages =
                pendingKycPackages,

            PendingExtensions =
                pendingExtensions,

            PendingQrPayments =
                pendingQrPayments,

            TodayPickups =
                await _dbContext.Bookings
                    .CountAsync(
                        booking =>
                            booking.PickupDate >=
                                today &&
                            booking.PickupDate <
                                tomorrow &&
                            (booking.Status ==
                                 BookingStatus.Paid ||
                             booking.Status ==
                                 BookingStatus
                                     .ReadyForPickup),
                        cancellationToken),

            TodayReturns =
                await _dbContext.Bookings
                    .CountAsync(
                        booking =>
                            booking.ReturnDate >=
                                today &&
                            booking.ReturnDate <
                                tomorrow &&
                            booking.Status ==
                                BookingStatus.Rented,
                        cancellationToken),

            ActiveRentals =
                await _dbContext.Bookings
                    .CountAsync(
                        booking =>
                            booking.Status ==
                            BookingStatus.Rented,
                        cancellationToken),

            MonthlyRevenue =
                await _dbContext.Payments
                    .Where(payment =>
                        payment.Status == PaymentStatus.Paid &&
                        (payment.Type == PaymentType.Rental ||
                         payment.Type == PaymentType.Extension ||
                         payment.Type == PaymentType.AdditionalCharge) &&
                        payment.PaidAt >= firstDayOfMonth &&
                        payment.PaidAt < firstDayOfNextMonth)
                    .SumAsync(
                        payment =>
                            (decimal?)payment.Amount,
                        cancellationToken)
                ?? 0,

            RecentBookings =
                recentBookings
        };
    }
}
