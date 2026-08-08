using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Services;

internal sealed class VehicleService : IVehicleService
{
    private static readonly BookingStatus[] BlockingBookingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;

    public VehicleService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<VehicleDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var vehicles = await VehicleQuery()
            .OrderByDescending(vehicle => vehicle.CreatedAt)
            .ToListAsync(cancellationToken);

        return vehicles.Select(ToDto).ToList();
    }

    public async Task<VehicleDto?> GetByIdAsync(
        int vehicleId,
        CancellationToken cancellationToken = default)
    {
        var vehicle = await VehicleQuery()
            .FirstOrDefaultAsync(item => item.VehicleId == vehicleId, cancellationToken);

        return vehicle is null ? null : ToDto(vehicle);
    }

    public async Task<IReadOnlyList<VehicleDto>> SearchAvailableAsync(
        VehicleSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.PickupDate >= request.ReturnDate)
        {
            return Array.Empty<VehicleDto>();
        }
        var query = VehicleQuery()
            .Where(vehicle => vehicle.Status == VehicleStatus.Available)
            .Where(vehicle => !vehicle.Bookings.Any(booking =>
                BlockingBookingStatuses.Contains(booking.Status) &&
                request.PickupDate < booking.ReturnDate &&
                request.ReturnDate > booking.PickupDate))
            .Where(vehicle => !vehicle.Incidents.Any(incident =>
                incident.Status != IncidentStatus.Resolved &&
                incident.Status != IncidentStatus.Cancelled))
            .Where(vehicle => vehicle.Documents.Any(document =>
                document.DocumentType == VehicleDocumentType.Registration &&
                (!document.ExpiryDate.HasValue || document.ExpiryDate.Value >= request.ReturnDate)))
            .Where(vehicle => vehicle.Documents.Any(document =>
                document.DocumentType == VehicleDocumentType.Inspection &&
                document.ExpiryDate.HasValue &&
                document.ExpiryDate.Value >= request.ReturnDate))
            .Where(vehicle => vehicle.Documents.Any(document =>
                document.DocumentType == VehicleDocumentType.Insurance &&
                document.ExpiryDate.HasValue &&
                document.ExpiryDate.Value >= request.ReturnDate));

        if (!string.IsNullOrWhiteSpace(
        request.PickupAddress))
        {
            var pickupAddress =
                request.PickupAddress.Trim();

            query = query.Where(vehicle =>
                vehicle.PickupAddress != null &&
                vehicle.PickupAddress.Contains(
                    pickupAddress));
        }
        if (request.BrandId.HasValue)
        {
            query = query.Where(vehicle => vehicle.BrandId == request.BrandId.Value);
        }

        if (request.Seats.HasValue)
        {
            query = query.Where(vehicle => vehicle.Seats >= request.Seats.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Transmission))
        {
            query = query.Where(vehicle => vehicle.Transmission == request.Transmission);
        }

        if (request.MaxDailyPrice.HasValue)
        {
            query = query.Where(vehicle => vehicle.DailyPrice <= request.MaxDailyPrice.Value);
        }

        var vehicles = await query
            .OrderBy(vehicle => vehicle.DailyPrice)
            .ToListAsync(cancellationToken);

        return vehicles.Select(ToDto).ToList();
    }

    public async Task<VehicleMutationResult> CreateAsync(
        CreateVehicleRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = await ValidateVehicleAsync(
            request.BrandId,
            request.VehicleName,
            request.LicensePlate,
            request.ManufactureYear,
            request.Seats,
            request.DailyPrice,
            request.CurrentMileage,
            null,
            cancellationToken);

        if (validationError is not null)
        {
            return VehicleMutationResult.Failure(validationError);
        }

        var vehicle = new Vehicle
        {
            BrandId = request.BrandId,
            VehicleName = request.VehicleName.Trim(),
            Model = NormalizeOptional(request.Model),
            LicensePlate = NormalizeLicensePlate(request.LicensePlate),
            ManufactureYear = request.ManufactureYear,
            Seats = request.Seats,
            Transmission = request.Transmission.Trim(),
            FuelType = request.FuelType.Trim(),
            Color = NormalizeOptional(request.Color),
            DailyPrice = request.DailyPrice,
            CurrentMileage = request.CurrentMileage,
            Status = VehicleStatus.Available,
            Description = NormalizeOptional(request.Description),
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Vehicles.Add(vehicle);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return VehicleMutationResult.Success(vehicle.VehicleId);
    }

    public async Task<OperationResult> UpdateAsync(
        UpdateVehicleRequest request,
        CancellationToken cancellationToken = default)
    {
        var vehicle = await _dbContext.Vehicles
            .FirstOrDefaultAsync(item => item.VehicleId == request.VehicleId, cancellationToken);

        if (vehicle is null)
        {
            return OperationResult.Failure("Không tìm thấy xe.");
        }

        if (vehicle.Status == VehicleStatus.Rented)
        {
            return OperationResult.Failure("Không thể sửa thông tin xe đang được thuê.");
        }

        var validationError = await ValidateVehicleAsync(
            request.BrandId,
            request.VehicleName,
            request.LicensePlate,
            request.ManufactureYear,
            request.Seats,
            request.DailyPrice,
            request.CurrentMileage,
            request.VehicleId,
            cancellationToken);

        if (validationError is not null)
        {
            return OperationResult.Failure(validationError);
        }

        _dbContext.Entry(vehicle)
            .Property(item => item.RowVersion)
            .OriginalValue = request.RowVersion;

        vehicle.BrandId = request.BrandId;
        vehicle.VehicleName = request.VehicleName.Trim();
        vehicle.Model = NormalizeOptional(request.Model);
        vehicle.LicensePlate = NormalizeLicensePlate(request.LicensePlate);
        vehicle.ManufactureYear = request.ManufactureYear;
        vehicle.Seats = request.Seats;
        vehicle.Transmission = request.Transmission.Trim();
        vehicle.FuelType = request.FuelType.Trim();
        vehicle.Color = NormalizeOptional(request.Color);
        vehicle.DailyPrice = request.DailyPrice;
        vehicle.CurrentMileage = request.CurrentMileage;
        vehicle.Description = NormalizeOptional(request.Description);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return OperationResult.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationResult.Failure(
                "Thông tin xe vừa được thay đổi bởi thao tác khác. Vui lòng tải lại trang.");
        }
    }

    public async Task<OperationResult> ChangeStatusAsync(
        int vehicleId,
        VehicleStatus status,
        CancellationToken cancellationToken = default)
    {
        var vehicle = await _dbContext.Vehicles
            .FirstOrDefaultAsync(item => item.VehicleId == vehicleId, cancellationToken);

        if (vehicle is null)
        {
            return OperationResult.Failure("Không tìm thấy xe.");
        }

        var hasActiveRental = await _dbContext.Bookings.AnyAsync(booking =>
            booking.VehicleId == vehicleId &&
            booking.Status == BookingStatus.Rented,
            cancellationToken);

        if (hasActiveRental && status != VehicleStatus.Rented)
        {
            return OperationResult.Failure("Không thể đổi trạng thái khi xe đang được khách thuê.");
        }

        vehicle.Status = status;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> AddImageAsync(
        int vehicleId,
        string imagePath,
        bool setAsPrimary,
        CancellationToken cancellationToken = default)
    {
        var vehicle = await _dbContext.Vehicles
            .Include(item => item.Images)
            .FirstOrDefaultAsync(item => item.VehicleId == vehicleId, cancellationToken);

        if (vehicle is null)
        {
            return OperationResult.Failure("Không tìm thấy xe.");
        }

        if (vehicle.Images.Count >= 10)
        {
            return OperationResult.Failure("Mỗi xe chỉ được lưu tối đa 10 ảnh.");
        }

        var shouldBePrimary = setAsPrimary || vehicle.Images.Count == 0;
        if (shouldBePrimary)
        {
            foreach (var image in vehicle.Images)
            {
                image.IsPrimary = false;
            }
        }

        vehicle.Images.Add(new VehicleImage
        {
            ImagePath = imagePath,
            IsPrimary = shouldBePrimary,
            SortOrder = vehicle.Images.Count
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> SetPrimaryImageAsync(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken = default)
    {
        var images = await _dbContext.VehicleImages
            .Where(image => image.VehicleId == vehicleId)
            .ToListAsync(cancellationToken);

        if (images.All(image => image.VehicleImageId != imageId))
        {
            return OperationResult.Failure("Không tìm thấy ảnh của xe.");
        }

        foreach (var image in images)
        {
            image.IsPrimary = image.VehicleImageId == imageId;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    public async Task<OperationResult> DeleteImageAsync(
        int vehicleId,
        int imageId,
        CancellationToken cancellationToken = default)
    {
        var images = await _dbContext.VehicleImages
            .Where(image => image.VehicleId == vehicleId)
            .OrderBy(image => image.SortOrder)
            .ToListAsync(cancellationToken);

        var image = images.FirstOrDefault(item => item.VehicleImageId == imageId);
        if (image is null)
        {
            return OperationResult.Failure("Không tìm thấy ảnh của xe.");
        }

        var wasPrimary = image.IsPrimary;
        _dbContext.VehicleImages.Remove(image);

        if (wasPrimary)
        {
            var replacement = images.FirstOrDefault(item => item.VehicleImageId != imageId);
            if (replacement is not null)
            {
                replacement.IsPrimary = true;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult.Success();
    }

    private IQueryable<Vehicle> VehicleQuery() =>
        _dbContext.Vehicles
            .AsNoTracking()
            .Include(vehicle => vehicle.Brand)
            .Include(vehicle => vehicle.Images)
            .Include(vehicle => vehicle.Bookings)
            .Include(vehicle => vehicle.Documents)
            .Include(vehicle => vehicle.Incidents);

    private async Task<string?> ValidateVehicleAsync(
        int brandId,
        string vehicleName,
        string licensePlate,
        int manufactureYear,
        int seats,
        decimal dailyPrice,
        int currentMileage,
        int? vehicleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vehicleName))
        {
            return "Tên xe không được để trống.";
        }

        if (string.IsNullOrWhiteSpace(licensePlate))
        {
            return "Biển số xe không được để trống.";
        }

        if (manufactureYear < 1980 || manufactureYear > DateTime.UtcNow.Year)
        {
            return "Năm sản xuất không hợp lệ.";
        }

        if (seats <= 0 || dailyPrice <= 0 || currentMileage < 0)
        {
            return "Số chỗ, giá thuê hoặc số km không hợp lệ.";
        }

        var brandExists = await _dbContext.Brands.AnyAsync(
            brand => brand.BrandId == brandId && brand.IsActive,
            cancellationToken);

        if (!brandExists)
        {
            return "Hãng xe không tồn tại hoặc đã ngừng hoạt động.";
        }

        var normalizedPlate = NormalizeLicensePlate(licensePlate);
        var plateExists = await _dbContext.Vehicles.AnyAsync(vehicle =>
            vehicle.LicensePlate == normalizedPlate &&
            (!vehicleId.HasValue || vehicle.VehicleId != vehicleId.Value),
            cancellationToken);

        return plateExists ? "Biển số xe đã tồn tại." : null;
    }

    private static VehicleDto ToDto(Vehicle vehicle) => new()
    {
        VehicleId = vehicle.VehicleId,
        BrandId = vehicle.BrandId,
        BrandName = vehicle.Brand.BrandName,
        VehicleName = vehicle.VehicleName,
        Model = vehicle.Model,
        LicensePlate = vehicle.LicensePlate,
        ManufactureYear = vehicle.ManufactureYear,
        Seats = vehicle.Seats,
        Transmission = vehicle.Transmission,
        FuelType = vehicle.FuelType,
        Color = vehicle.Color,
        DailyPrice = vehicle.DailyPrice,
        PickupAddress = vehicle.PickupAddress,
        CurrentMileage = vehicle.CurrentMileage,
        Status = vehicle.Status,
        Description = vehicle.Description,
        PrimaryImagePath = vehicle.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .Select(image => image.ImagePath)
            .FirstOrDefault(),
        RowVersion = vehicle.RowVersion,
        Images = vehicle.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .Select(image => new VehicleImageDto(
                image.VehicleImageId,
                image.ImagePath,
                image.IsPrimary,
                image.SortOrder))
            .ToList()
    };

    private static string NormalizeLicensePlate(string value) =>
        value.Trim().ToUpperInvariant().Replace(" ", string.Empty);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
