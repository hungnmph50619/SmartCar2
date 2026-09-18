using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Common;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Application.Features.VehicleDocuments;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Constants;
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

    private static readonly VehicleStatus[] StatusesRequiringLegalEligibility =
    {
        VehicleStatus.Available,
        VehicleStatus.Rented
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IVehicleDocumentService _vehicleDocumentService;

    public VehicleService(
        ApplicationDbContext dbContext,
        IVehicleDocumentService vehicleDocumentService)
    {
        _vehicleDocumentService = vehicleDocumentService;
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

        var preparationBeforeRequested = TimeSpan.FromMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(request.PickupMethod));
        var requestedPickupBoundary = request.PickupDate - preparationBeforeRequested;
        var requestedReturnWithStorePreparation = request.ReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.StorePickup));
        var requestedReturnWithDeliveryPreparation = request.ReturnDate.AddMinutes(
            RentalPolicy.GetOperationalPreparationMinutes(VehiclePickupMethod.Delivery));

        var query = VehicleQuery()
            .Where(vehicle =>
                vehicle.Status == VehicleStatus.Available ||
                vehicle.Status == VehicleStatus.Rented)
            .Where(vehicle => !vehicle.Bookings.Any(booking =>
                BlockingBookingStatuses.Contains(booking.Status) &&
                requestedPickupBoundary < booking.ReturnDate &&
                (
                    booking.PickupMethod == VehiclePickupMethod.Delivery
                        ? requestedReturnWithDeliveryPreparation > booking.PickupDate
                        : requestedReturnWithStorePreparation > booking.PickupDate
                )))
            // Phạt nguội là nghĩa vụ tài chính gắn với chuyến thuê/khách cũ,
            // không phải hỏng hóc vật lý của xe nên không được chặn xe tiếp tục cho thuê.
            .Where(vehicle => !vehicle.Incidents.Any(incident =>
                incident.IncidentType != IncidentType.TrafficFine &&
                incident.Status != IncidentStatus.Resolved &&
                incident.Status != IncidentStatus.Cancelled))
            .Where(vehicle => vehicle.Documents.Any(document =>
                document.DocumentType == VehicleDocumentType.Registration &&
                document.IssuedDate.Date <= request.PickupDate.Date &&
                (!document.ExpiryDate.HasValue ||
                 document.ExpiryDate.Value.Date >= request.ReturnDate.Date)))
            .Where(vehicle => vehicle.Documents.Any(document =>
                document.DocumentType == VehicleDocumentType.Inspection &&
                document.IssuedDate.Date <= request.PickupDate.Date &&
                document.ExpiryDate.HasValue &&
                document.ExpiryDate.Value.Date >= request.ReturnDate.Date))
            .Where(vehicle => vehicle.Documents.Any(document =>
                document.DocumentType == VehicleDocumentType.Insurance &&
                document.IssuedDate.Date <= request.PickupDate.Date &&
                document.ExpiryDate.HasValue &&
                document.ExpiryDate.Value.Date >= request.ReturnDate.Date))
            .Where(vehicle => vehicle.Documents.Any(document =>
                document.DocumentType == VehicleDocumentType.RoadFee &&
                document.IssuedDate.Date <= request.PickupDate.Date &&
                document.ExpiryDate.HasValue &&
                document.ExpiryDate.Value.Date >= request.ReturnDate.Date));

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

        if (!string.IsNullOrWhiteSpace(request.FuelType))
        {
            query = query.Where(vehicle => vehicle.FuelType == request.FuelType);
        }

        if (request.MinDailyPrice.HasValue)
        {
            query = query.Where(vehicle => vehicle.DailyPrice >= request.MinDailyPrice.Value);
        }

        if (request.MaxDailyPrice.HasValue)
        {
            query = query.Where(vehicle => vehicle.DailyPrice <= request.MaxDailyPrice.Value);
        }

        if (request.MinManufactureYear.HasValue)
        {
            query = query.Where(vehicle => vehicle.ManufactureYear >= request.MinManufactureYear.Value);
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
            // Xe mới tạo chưa có giấy tờ pháp lý nào (Đăng ký/Đăng kiểm/Bảo hiểm/Phí đường bộ),
            // nên không thể ở trạng thái Sẵn sàng/Cho thuê ngay. Admin cần bổ sung đủ giấy tờ
            // rồi chuyển trạng thái thủ công qua ChangeStatusAsync (đã có kiểm tra pháp lý).
            Status = VehicleStatus.Inactive,
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

        var hasBlockingBooking = await _dbContext.Bookings.AnyAsync(booking =>
            booking.VehicleId == vehicleId &&
            BlockingBookingStatuses.Contains(booking.Status),
            cancellationToken);

        if (hasBlockingBooking && status != vehicle.Status)
        {
            return OperationResult.Failure(
                "Không thể tùy ý đổi trạng thái xe khi đang có đơn thuê chưa hoàn tất.");
        }

        // BUG FIX: Admin không được mở lại xe (Available) nếu hồ sơ pháp lý chưa đủ.
        // Kiểm tra này nằm ở service/server, không phụ thuộc vào việc UI có disable nút hay không.
        if (status == VehicleStatus.Available)
        {
            var legalStatus = (await _vehicleDocumentService
                .GetOverviewByVehicleAsync(vehicleId, cancellationToken))
                .LegalStatus;

            if (!legalStatus.IsEligible)
            {
                var reasonText = legalStatus.Reasons.Count > 0
                    ? string.Join(" ", legalStatus.Reasons)
                    : "Giấy tờ pháp lý của xe chưa đầy đủ hoặc đã hết hạn.";

                return OperationResult.Failure(
                    $"Không thể mở lại hoạt động cho xe vì xe chưa đủ điều kiện pháp lý cho thuê. {reasonText}");
            }

            var hasOpenIncident = await _dbContext.VehicleIncidents.AnyAsync(incident =>
                incident.VehicleId == vehicleId &&
                incident.IncidentType != IncidentType.TrafficFine &&
                incident.Status != IncidentStatus.Resolved &&
                incident.Status != IncidentStatus.Cancelled,
                cancellationToken);

            if (hasOpenIncident)
            {
                return OperationResult.Failure(
                    "Không thể mở lại hoạt động cho xe vì xe vẫn còn sự cố vận hành chưa được xử lý.");
            }
        }
        else if (StatusesRequiringLegalEligibility.Contains(status))
        {
            var legalStatus = (await _vehicleDocumentService
                .GetOverviewByVehicleAsync(vehicleId, cancellationToken))
                .LegalStatus;

            if (!legalStatus.IsEligible)
            {
                var reasonText = legalStatus.Reasons.Count > 0
                    ? string.Join(" ", legalStatus.Reasons)
                    : "Giấy tờ pháp lý của xe chưa đầy đủ hoặc đã hết hạn.";

                return OperationResult.Failure(
                    $"Không thể chuyển xe sang trạng thái này vì xe chưa đủ điều kiện pháp lý cho thuê. {reasonText}");
            }
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
            return $"Năm sản xuất phải từ 1980 đến {DateTime.UtcNow.Year}.";
        }

        int[] allowedSeats = [2, 4, 5, 7, 8, 9, 16];
        if (!allowedSeats.Contains(seats))
        {
            return "Số chỗ không hợp lệ. Vui lòng chọn 2, 4, 5, 7, 8, 9 hoặc 16 chỗ.";
        }

        const decimal maxDailyPrice = 100_000_000m;
        if (dailyPrice < 1 || dailyPrice > maxDailyPrice)
        {
            return "Giá thuê/ngày phải từ 1 đến 100.000.000 đồng.";
        }

        const int maxCurrentMileage = 2_000_000;
        if (currentMileage < 0 || currentMileage > maxCurrentMileage)
        {
            return "Số km hiện tại phải từ 0 đến 2.000.000 km.";
        }

        var brand = await _dbContext.Brands
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.BrandId == brandId, cancellationToken);

        if (brand is null)
        {
            return "Hãng xe không tồn tại.";
        }

        if (!brand.IsActive)
        {
            // Khi tạo xe, hãng luôn phải đang hoạt động.
            if (!vehicleId.HasValue)
            {
                return "Hãng xe đã ngừng hoạt động.";
            }

            // Khi sửa, được giữ nguyên hãng cũ dù hãng vừa bị ngừng,
            // nhưng không được chuyển sang một hãng inactive khác.
            var currentBrandId = await _dbContext.Vehicles
                .Where(vehicle => vehicle.VehicleId == vehicleId.Value)
                .Select(vehicle => vehicle.BrandId)
                .FirstOrDefaultAsync(cancellationToken);

            if (currentBrandId != brandId)
            {
                return "Không thể chuyển xe sang một hãng đã ngừng hoạt động.";
            }
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
