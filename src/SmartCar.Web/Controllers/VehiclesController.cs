using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Route("Vehicles")]
public class VehiclesController : Controller
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

    public VehiclesController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] VehicleCatalogQueryViewModel query,
        CancellationToken cancellationToken = default)
    {
        var (pickupDateTime, returnDateTime) = BuildDateRange(
            query.PickupDate,
            query.PickupTime,
            query.ReturnDate,
            query.ReturnTime);

        query.PickupDate = pickupDateTime.Date;
        query.PickupTime = pickupDateTime.ToString("HH:mm");
        query.ReturnDate = returnDateTime.Date;
        query.ReturnTime = returnDateTime.ToString("HH:mm");
        query.PickupLocation = string.IsNullOrWhiteSpace(query.PickupLocation)
            ? "SmartCar Cầu Giấy"
            : query.PickupLocation.Trim();

        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .Include(vehicle => vehicle.Brand)
            .Include(vehicle => vehicle.Images)
            .Include(vehicle => vehicle.Bookings)
                .ThenInclude(booking => booking.Review)
            .Where(vehicle => vehicle.Status == VehicleStatus.Available)
            .ToListAsync(cancellationToken);

        var availableVehicles = vehicles
            .Where(vehicle => !vehicle.Bookings.Any(booking =>
                BlockingBookingStatuses.Contains(booking.Status)
                && pickupDateTime < booking.ReturnDate
                && returnDateTime > booking.PickupDate))
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query.Brand))
        {
            availableVehicles = availableVehicles.Where(vehicle =>
                string.Equals(vehicle.Brand.BrandName, query.Brand, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Seats.HasValue)
        {
            availableVehicles = availableVehicles.Where(vehicle => vehicle.Seats == query.Seats.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Transmission))
        {
            availableVehicles = availableVehicles.Where(vehicle =>
                vehicle.Transmission.Contains(query.Transmission, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.FuelType))
        {
            availableVehicles = availableVehicles.Where(vehicle =>
                vehicle.FuelType.Contains(query.FuelType, StringComparison.OrdinalIgnoreCase));
        }

        if (query.ManufactureYearFrom.HasValue)
        {
            availableVehicles = availableVehicles.Where(vehicle =>
                vehicle.ManufactureYear >= query.ManufactureYearFrom.Value);
        }

        if (query.MinPrice.HasValue)
        {
            availableVehicles = availableVehicles.Where(vehicle =>
                vehicle.DailyPrice >= query.MinPrice.Value);
        }

        if (query.MaxPrice.HasValue)
        {
            availableVehicles = availableVehicles.Where(vehicle =>
                vehicle.DailyPrice <= query.MaxPrice.Value);
        }

        availableVehicles = query.Sort switch
        {
            "price-asc" => availableVehicles.OrderBy(vehicle => vehicle.DailyPrice),
            "price-desc" => availableVehicles.OrderByDescending(vehicle => vehicle.DailyPrice),
            "newest" => availableVehicles.OrderByDescending(vehicle => vehicle.ManufactureYear),
            "popular" => availableVehicles.OrderByDescending(vehicle =>
                vehicle.Bookings.Count(booking => booking.Status == BookingStatus.Completed)),
            "rating" => availableVehicles.OrderByDescending(GetVehicleRating),
            _ => availableVehicles
                .OrderByDescending(GetVehicleRating)
                .ThenBy(vehicle => vehicle.DailyPrice)
        };

        var cards = availableVehicles
            .Select(MapVehicleCard)
            .ToList();

        var brands = vehicles
            .Select(vehicle => vehicle.Brand.BrandName)
            .Where(brand => !string.IsNullOrWhiteSpace(brand))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(brand => brand)
            .ToList();

        var model = new VehicleCatalogViewModel
        {
            Query = query,
            Vehicles = cards,
            Brands = brands,
            TotalCount = cards.Count,
            PickupDateTime = pickupDateTime,
            ReturnDateTime = returnDateTime,
            SearchSummary = $"{query.PickupLocation} · {pickupDateTime:HH:mm dd/MM/yyyy} – {returnDateTime:HH:mm dd/MM/yyyy}"
        };

        return View(model);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(
        int id,
        string? pickupLocation,
        DateTime? pickupDate,
        string? pickupTime,
        DateTime? returnDate,
        string? returnTime,
        CancellationToken cancellationToken = default)
    {
        var (pickupDateTime, returnDateTime) = BuildDateRange(
            pickupDate,
            pickupTime,
            returnDate,
            returnTime);

        var vehicle = await _dbContext.Vehicles
            .AsNoTracking()
            .Include(item => item.Brand)
            .Include(item => item.Images)
            .Include(item => item.Bookings)
                .ThenInclude(booking => booking.Review)
            .FirstOrDefaultAsync(item => item.VehicleId == id, cancellationToken);

        if (vehicle is null || vehicle.Status != VehicleStatus.Available)
        {
            return NotFound();
        }

        var similarVehicleCandidates = await _dbContext.Vehicles
            .AsNoTracking()
            .Include(item => item.Brand)
            .Include(item => item.Images)
            .Include(item => item.Bookings)
                .ThenInclude(booking => booking.Review)
            .Where(item =>
                item.VehicleId != id
                && item.Status == VehicleStatus.Available
                && (item.Seats == vehicle.Seats || item.BrandId == vehicle.BrandId))
            .ToListAsync(cancellationToken);

        var similarVehicles = similarVehicleCandidates
            .Where(item => !item.Bookings.Any(booking =>
                BlockingBookingStatuses.Contains(booking.Status)
                && pickupDateTime < booking.ReturnDate
                && returnDateTime > booking.PickupDate))
            .Take(4)
            .ToList();

        var rentalDays = Math.Max(1, (int)Math.Ceiling((returnDateTime - pickupDateTime).TotalDays));
        var rentalAmount = vehicle.DailyPrice * rentalDays;
        var normalizedLocation = string.IsNullOrWhiteSpace(pickupLocation)
            ? "SmartCar Cầu Giấy"
            : pickupLocation.Trim();
        var deliveryFee = normalizedLocation.Equals("SmartCar Cầu Giấy", StringComparison.OrdinalIgnoreCase)
            ? 0m
            : 100_000m;
        const decimal depositAmount = 500_000m;

        var completedReviews = vehicle.Bookings
            .Where(booking => booking.Status == BookingStatus.Completed && booking.Review is not null)
            .Select(booking => booking.Review!)
            .OrderByDescending(review => review.CreatedAt)
            .ToList();

        var images = BuildImages(vehicle);

        var model = new VehicleDetailsViewModel
        {
            VehicleId = vehicle.VehicleId,
            Name = vehicle.VehicleName,
            Brand = vehicle.Brand.BrandName,
            Model = vehicle.Model,
            LicensePlate = vehicle.LicensePlate,
            ManufactureYear = vehicle.ManufactureYear,
            Seats = vehicle.Seats,
            Transmission = vehicle.Transmission,
            FuelType = vehicle.FuelType,
            Color = vehicle.Color,
            CurrentMileage = vehicle.CurrentMileage,
            DailyPrice = vehicle.DailyPrice,
            Description = string.IsNullOrWhiteSpace(vehicle.Description)
                ? "Xe thuộc đội xe SmartCar, được kiểm tra trước mỗi chuyến và áp dụng chính sách giao nhận thống nhất."
                : vehicle.Description,
            Rating = completedReviews.Count == 0 ? 5 : completedReviews.Average(review => review.Rating),
            RentalCount = vehicle.Bookings.Count(booking => booking.Status == BookingStatus.Completed),
            PickupDateTime = pickupDateTime,
            ReturnDateTime = returnDateTime,
            PickupLocation = normalizedLocation,
            RentalDays = rentalDays,
            RentalAmount = rentalAmount,
            DeliveryFee = deliveryFee,
            DepositAmount = depositAmount,
            EstimatedTotal = rentalAmount + deliveryFee + depositAmount,
            Images = images,
            Reviews = completedReviews.Take(8).Select(review => new VehicleReviewItemViewModel
            {
                Rating = review.Rating,
                Comment = string.IsNullOrWhiteSpace(review.Comment)
                    ? "Xe sạch, đúng thông tin và quy trình giao nhận rõ ràng."
                    : review.Comment,
                CreatedAt = review.CreatedAt,
                CustomerName = "Khách hàng SmartCar"
            }).ToList(),
            SimilarVehicles = similarVehicles.Select(MapVehicleCard).ToList()
        };

        return View(model);
    }

    [Authorize]
    [HttpPost("RequestRental")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestRental(
        CreateRentalRequestViewModel model,
        CancellationToken cancellationToken = default)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        model.PickupLocation = string.IsNullOrWhiteSpace(model.PickupLocation)
            ? "SmartCar Cầu Giấy"
            : model.PickupLocation.Trim();

        var (pickupDateTime, returnDateTime) = BuildDateRange(
            model.PickupDate,
            model.PickupTime,
            model.ReturnDate,
            model.ReturnTime);

        if (pickupDateTime <= DateTime.Now || returnDateTime <= pickupDateTime)
        {
            TempData["ErrorMessage"] = "Thời gian nhận và trả xe không hợp lệ.";
            return RedirectToAction(nameof(Details), new
            {
                id = model.VehicleId,
                model.PickupLocation,
                model.PickupDate,
                model.PickupTime,
                model.ReturnDate,
                model.ReturnTime
            });
        }

        var vehicle = await _dbContext.Vehicles
            .FirstOrDefaultAsync(item =>
                item.VehicleId == model.VehicleId
                && item.Status == VehicleStatus.Available,
                cancellationToken);

        if (vehicle is null)
        {
            TempData["ErrorMessage"] = "Xe không còn khả dụng. Vui lòng chọn xe khác.";
            return RedirectToAction(nameof(Index));
        }

        var hasConflict = await _dbContext.Bookings.AnyAsync(booking =>
            booking.VehicleId == model.VehicleId
            && BlockingBookingStatuses.Contains(booking.Status)
            && pickupDateTime < booking.ReturnDate
            && returnDateTime > booking.PickupDate,
            cancellationToken);

        if (hasConflict)
        {
            TempData["ErrorMessage"] = "Xe vừa có lịch giữ chỗ trùng thời gian. Vui lòng chọn thời gian hoặc xe khác.";
            return RedirectToAction(nameof(Details), new
            {
                id = model.VehicleId,
                model.PickupLocation,
                model.PickupDate,
                model.PickupTime,
                model.ReturnDate,
                model.ReturnTime
            });
        }

        var rentalDays = Math.Max(1, (int)Math.Ceiling((returnDateTime - pickupDateTime).TotalDays));
        var deliveryFee = string.Equals(
            model.PickupLocation,
            "SmartCar Cầu Giấy",
            StringComparison.OrdinalIgnoreCase)
            ? 0m
            : 100_000m;
        var rentalAmount = vehicle.DailyPrice * rentalDays;

        var booking = new Booking
        {
            CustomerId = customerId,
            VehicleId = vehicle.VehicleId,
            PickupDate = pickupDateTime,
            ReturnDate = returnDateTime,
            DailyPrice = vehicle.DailyPrice,
            NumberOfDays = rentalDays,
            RentalAmount = rentalAmount,
            AdditionalAmount = deliveryFee,
            TotalAmount = rentalAmount + deliveryFee,
            Status = BookingStatus.PendingConfirmation,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Bookings.Add(booking);
        _dbContext.Notifications.Add(new Notification
        {
            UserId = customerId,
            Title = "SmartCar đã tiếp nhận yêu cầu thuê xe",
            Message = $"Yêu cầu thuê {vehicle.VehicleName} đã được ghi nhận. SmartCar đang kiểm tra lịch và tình trạng xe.",
            CreatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AccountSuccessMessage"] =
            "Đã gửi yêu cầu thuê xe. SmartCar sẽ kiểm tra và cập nhật kết quả trong mục Đơn thuê của tôi.";

        return RedirectToAction("Profile", "CustomerAccount", new { section = "bookings" });
    }

    private static (DateTime Pickup, DateTime Return) BuildDateRange(
        DateTime? pickupDate,
        string? pickupTime,
        DateTime? returnDate,
        string? returnTime)
    {
        var pickupDay = pickupDate?.Date ?? DateTime.Today.AddDays(1);
        var returnDay = returnDate?.Date ?? pickupDay.AddDays(1);
        var pickupClock = ParseTime(pickupTime, new TimeSpan(8, 0, 0));
        var returnClock = ParseTime(returnTime, new TimeSpan(18, 0, 0));
        var pickup = pickupDay.Add(pickupClock);
        var returnValue = returnDay.Add(returnClock);

        if (pickup <= DateTime.Now)
        {
            pickup = DateTime.Today.AddDays(1).AddHours(8);
        }

        if (returnValue <= pickup)
        {
            returnValue = pickup.Date.AddDays(1).AddHours(18);
        }

        return (pickup, returnValue);
    }

    private static TimeSpan ParseTime(string? value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, out var parsed) ? parsed : fallback;

    private static VehicleCardViewModel MapVehicleCard(Vehicle vehicle)
    {
        var completedReviews = vehicle.Bookings
            .Where(booking => booking.Status == BookingStatus.Completed && booking.Review is not null)
            .Select(booking => booking.Review!)
            .ToList();

        var primaryImage = vehicle.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .FirstOrDefault();

        return new VehicleCardViewModel
        {
            VehicleId = vehicle.VehicleId,
            Name = vehicle.VehicleName,
            Brand = vehicle.Brand.BrandName,
            ManufactureYear = vehicle.ManufactureYear,
            Seats = vehicle.Seats,
            Transmission = vehicle.Transmission,
            FuelType = vehicle.FuelType,
            DailyPrice = vehicle.DailyPrice,
            ImageUrl = primaryImage is null
                ? BuildPlaceholderImage(vehicle.VehicleName, GetVehicleColor(vehicle.Color))
                : NormalizeImagePath(primaryImage.ImagePath),
            Rating = completedReviews.Count == 0 ? 5 : completedReviews.Average(review => review.Rating),
            RentalCount = vehicle.Bookings.Count(booking => booking.Status == BookingStatus.Completed),
            Badge = vehicle.ManufactureYear >= DateTime.Today.Year - 1 ? "Xe mới" : "Còn trống"
        };
    }

    private static IReadOnlyList<VehicleImageItemViewModel> BuildImages(Vehicle vehicle)
    {
        var storedImages = vehicle.Images
            .OrderByDescending(image => image.IsPrimary)
            .ThenBy(image => image.SortOrder)
            .ToList();

        if (storedImages.Count > 0)
        {
            return storedImages.Select((image, index) => new VehicleImageItemViewModel
            {
                Url = NormalizeImagePath(image.ImagePath),
                Caption = GetImageCaption(index),
                Category = GetImageCategory(index)
            }).ToList();
        }

        return Enumerable.Range(0, 8).Select(index => new VehicleImageItemViewModel
        {
            Url = BuildPlaceholderImage(vehicle.VehicleName, GetPlaceholderColor(index)),
            Caption = GetImageCaption(index),
            Category = GetImageCategory(index)
        }).ToList();
    }

    private static string NormalizeImagePath(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return string.Empty;
        }

        if (imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith('/'))
        {
            return imagePath;
        }

        return "/" + imagePath.TrimStart('~', '/');
    }

    private static double GetVehicleRating(Vehicle vehicle)
    {
        var ratings = vehicle.Bookings
            .Where(booking => booking.Review is not null)
            .Select(booking => booking.Review!.Rating)
            .ToList();

        return ratings.Count == 0 ? 5 : ratings.Average();
    }

    private static string GetImageCaption(int index) => index switch
    {
        0 => "Góc trước bên trái",
        1 => "Chính diện phía trước",
        2 => "Góc sau bên phải",
        3 => "Chính diện phía sau",
        4 => "Khoang lái và vô-lăng",
        5 => "Hàng ghế trước",
        6 => "Hàng ghế sau",
        _ => "Khoang hành lý và tình trạng xe"
    };

    private static string GetImageCategory(int index) => index switch
    {
        <= 3 => "Ngoại thất",
        <= 6 => "Nội thất",
        7 => "Khoang hành lý",
        _ => "Tình trạng xe"
    };

    private static string GetVehicleColor(string? color) => color?.Trim().ToLowerInvariant() switch
    {
        "trắng" => "#dfe7ef",
        "đen" => "#111827",
        "bạc" => "#7b8794",
        "đỏ" => "#b42318",
        "xanh" => "#0f5c78",
        "xanh đậm" => "#123d6a",
        _ => "#123d6a"
    };

    private static string GetPlaceholderColor(int index) => index switch
    {
        0 => "#123d6a",
        1 => "#0f5c78",
        2 => "#3f536a",
        3 => "#27415f",
        4 => "#1b2638",
        5 => "#4f5d6d",
        6 => "#35495e",
        _ => "#18324f"
    };

    private static string BuildPlaceholderImage(string vehicleName, string color)
    {
        var safeName = vehicleName.Replace("&", "và").Replace("<", string.Empty).Replace(">", string.Empty);
        var svg = $"""
            <svg xmlns='http://www.w3.org/2000/svg' width='1200' height='760' viewBox='0 0 1200 760'>
              <defs>
                <linearGradient id='bg' x1='0' x2='1' y1='0' y2='1'>
                  <stop offset='0' stop-color='#eef4fb'/>
                  <stop offset='1' stop-color='#d9e5f1'/>
                </linearGradient>
                <linearGradient id='car' x1='0' x2='1'>
                  <stop offset='0' stop-color='{color}'/>
                  <stop offset='1' stop-color='#061a32'/>
                </linearGradient>
              </defs>
              <rect width='1200' height='760' fill='url(#bg)'/>
              <path d='M0 570h1200v190H0z' fill='#bdc9d4'/>
              <path d='M85 570h190v190H85zm380 0h190v190H465zm380 0h190v190H845z' fill='#eaf0f5'/>
              <g transform='translate(120 175)'>
                <path d='M115 285c30-104 88-170 190-210h365c83 21 151 87 195 210l85 25c42 12 70 50 70 94v38H15v-42c0-53 36-99 87-112z' fill='url(#car)'/>
                <path d='M327 102h310c55 18 101 62 133 133H235c21-59 49-103 92-133z' fill='#9bd6f4' opacity='.72'/>
                <path d='M507 105v130' stroke='#eaf8ff' stroke-width='9'/>
                <circle cx='235' cy='430' r='80' fill='#172536'/>
                <circle cx='235' cy='430' r='38' fill='#9aabb9'/>
                <circle cx='790' cy='430' r='80' fill='#172536'/>
                <circle cx='790' cy='430' r='38' fill='#9aabb9'/>
                <path d='M855 318h118' stroke='#c9f5ff' stroke-width='22' stroke-linecap='round'/>
              </g>
              <text x='60' y='78' font-family='Arial, sans-serif' font-size='36' font-weight='700' fill='#082554'>{safeName}</text>
              <text x='60' y='122' font-family='Arial, sans-serif' font-size='22' fill='#52657a'>Ảnh minh họa — thay bằng ảnh thực tế trong quản trị xe</text>
            </svg>
            """;

        return "data:image/svg+xml;charset=UTF-8," + Uri.EscapeDataString(svg);
    }
}
