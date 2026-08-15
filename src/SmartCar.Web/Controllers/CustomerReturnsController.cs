using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class CustomerReturnsController : Controller
{
    private const int MinimumImages = 6;
    private const int MaximumImages = 12;
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> FuelGaugeLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "8/8 (100%)",
        "7/8 (~87.5%)",
        "6/8 (75%)",
        "5/8 (~62.5%)",
        "4/8 (50%)",
        "3/8 (~37.5%)",
        "2/8 (25%)",
        "1/8 (~12.5%)",
        "0/8 (Gần cạn)"
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;

    public CustomerReturnsController(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment)
    {
        _dbContext = dbContext;
        _environment = environment;
    }

    [HttpGet]
    public async Task<IActionResult> CheckOut(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.Rented || booking.Handover is null)
        {
            TempData["ErrorMessage"] = "Chỉ chuyến đang thuê và đã check-in nhận xe mới có thể thực hiện check-out trả xe.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var existing = await VehicleEvidenceAuditHelper.GetCheckOutAsync(
            _dbContext,
            bookingId,
            cancellationToken);
        if (existing is not null)
        {
            TempData["SuccessMessage"] = "Bạn đã hoàn tất check-out. Hãy bàn giao xe/chìa khóa để SmartCar thực hiện kiểm tra độc lập.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var checkIn = await VehicleEvidenceAuditHelper.GetCheckInAsync(
            _dbContext,
            bookingId,
            cancellationToken);
        SetCheckOutContext(booking, checkIn);

        return View(new CustomerVehicleCheckOutViewModel
        {
            BookingId = bookingId,
            Mileage = booking.Handover.Mileage
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckOut(
        CustomerVehicleCheckOutViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .Include(item => item.Vehicle)
            .Include(item => item.Handover)
            .FirstOrDefaultAsync(item =>
                item.BookingId == model.BookingId && item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        var checkIn = await VehicleEvidenceAuditHelper.GetCheckInAsync(
            _dbContext,
            model.BookingId,
            cancellationToken);
        SetCheckOutContext(booking, checkIn);

        if (booking.Status != BookingStatus.Rented || booking.Handover is null)
        {
            ModelState.AddModelError(string.Empty, "Đơn không còn ở trạng thái Đang thuê để thực hiện check-out.");
        }

        if (await VehicleEvidenceAuditHelper.HasCheckOutAsync(_dbContext, model.BookingId, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, "Bộ bằng chứng check-out của chuyến này đã được khóa trước đó.");
        }

        if (!model.Accepted)
        {
            ModelState.AddModelError(
                nameof(CustomerVehicleCheckOutViewModel.Accepted),
                "Vui lòng xác nhận bộ ảnh check-out phản ánh chiếc xe tại thời điểm bạn chuẩn bị bàn giao lại cho SmartCar.");
        }

        if (booking.Handover is not null && model.Mileage < booking.Handover.Mileage)
        {
            ModelState.AddModelError(
                nameof(CustomerVehicleCheckOutViewModel.Mileage),
                $"ODO check-out không được nhỏ hơn ODO lúc nhận ({booking.Handover.Mileage:N0} km)."
            );
        }

        var fuelResult = NormalizeFuelLevel(booking.Vehicle.FuelType, model.FuelLevel);
        if (!fuelResult.Succeeded)
        {
            ModelState.AddModelError(nameof(CustomerVehicleCheckOutViewModel.FuelLevel), fuelResult.Error!);
        }
        else
        {
            model.FuelLevel = fuelResult.Value!;
        }

        if (model.HasIssue && string.IsNullOrWhiteSpace(model.Note))
        {
            ModelState.AddModelError(
                nameof(CustomerVehicleCheckOutViewModel.Note),
                "Nếu xe có hư hỏng/bất thường khi trả, vui lòng mô tả rõ vị trí và tình trạng.");
        }

        await ValidateImagesAsync(model.EvidenceImages, cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var imagePaths = await SaveImagesAsync(model.BookingId, model.EvidenceImages, cancellationToken);
        var recordedAt = DateTime.UtcNow;
        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 500)
        {
            userAgent = userAgent[..500];
        }

        var evidenceHash = await ComputeEvidenceHashAsync(
            model.BookingId,
            model.Mileage,
            model.FuelLevel,
            model.HasIssue,
            model.Note,
            imagePaths,
            cancellationToken);

        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = customerId,
            Action = VehicleEvidenceAuditHelper.CustomerCheckOutCompleted,
            EntityName = nameof(Booking),
            EntityId = model.BookingId.ToString(),
            Description =
                $"Khách tự thực hiện check-out đơn #{model.BookingId} bằng {imagePaths.Count} ảnh; " +
                $"ODO {model.Mileage:N0} km; nhiên liệu/pin {model.FuelLevel}; " +
                $"bất thường do khách khai: {(model.HasIssue ? "Có" : "Không")}; evidence SHA-256 {evidenceHash}. " +
                "Booking vẫn Rented cho đến khi SmartCar thực tế nhận xe/chìa khóa và lập biên bản kiểm tra độc lập.",
            NewValues = JsonSerializer.Serialize(new
            {
                model.BookingId,
                Mileage = model.Mileage,
                FuelLevel = model.FuelLevel,
                HasIssue = model.HasIssue,
                Note = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim(),
                ImagePaths = string.Join(';', imagePaths),
                EvidenceHash = evidenceHash,
                CheckedOutAt = recordedAt,
                UserAgent = userAgent
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = recordedAt
        });

        await AddAdminNotificationsAsync(
            $"Khách đã check-out xe|Booking:{model.BookingId}",
            $"Khách đã khóa bộ ảnh check-out đơn #{model.BookingId} ({imagePaths.Count} ảnh, ODO {model.Mileage:N0} km). " +
            "Admin có thể tiếp nhận xe thực tế và chụp bộ ảnh kiểm tra độc lập để đối chiếu.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] =
            "Check-out hoàn tất. Bộ ảnh do chính tài khoản của bạn tạo đã được khóa; hãy bàn giao xe và chìa khóa để SmartCar kiểm tra độc lập.";
        return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
    }

    [HttpGet]
    public async Task<IActionResult> Review(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var bookingStatus = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId && item.CustomerId == customerId)
            .Select(item => (BookingStatus?)item.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (!bookingStatus.HasValue)
        {
            return NotFound();
        }

        if (bookingStatus.Value is not (BookingStatus.PendingInspection or BookingStatus.Completed))
        {
            TempData["ErrorMessage"] = "Biên bản đối chiếu trả xe chỉ khả dụng sau khi SmartCar thực tế tiếp nhận xe.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var model = await ReturnEvidenceHelper.BuildAsync(
            _dbContext,
            bookingId,
            customerId,
            cancellationToken);

        if (model is null)
        {
            return NotFound();
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở bước chờ kiểm tra sau trả xe.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var latestAction = await GetLatestReviewActionAsync(bookingId, cancellationToken);
        if (latestAction != ReturnEvidenceHelper.PendingAction)
        {
            TempData["ErrorMessage"] = latestAction switch
            {
                ReturnEvidenceHelper.AcceptedAction => "Bạn đã xác nhận hiện trạng trả xe trước đó.",
                ReturnEvidenceHelper.DisputedAction => "Bạn đã gửi yêu cầu xem xét. Hãy chờ SmartCar xử lý phản hồi.",
                ReturnEvidenceHelper.ResolvedAction => "Phản hồi về hiện trạng trả xe đã được SmartCar xử lý.",
                _ => "Biên bản này không thuộc luồng xác nhận hiện trạng mới."
            };
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var createdAt = DateTime.UtcNow;
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = customerId,
            Action = ReturnEvidenceHelper.AcceptedAction,
            EntityName = nameof(VehicleReturn),
            EntityId = bookingId.ToString(),
            Description =
                $"Khách đã xem bộ ảnh check-in/check-out của chính mình cùng ảnh kiểm tra độc lập của SmartCar cho đơn #{bookingId}; xác nhận hiện trạng trả xe để SmartCar tiếp tục kiểm tra, quyết toán.",
            NewValues = JsonSerializer.Serialize(new
            {
                bookingId,
                AcceptedAt = createdAt
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = createdAt
        });

        await AddAdminNotificationsAsync(
            $"Khách đã xác nhận hiện trạng trả xe|Booking:{bookingId}",
            $"Khách đã xem bộ bằng chứng check-in ↔ check-out và ảnh kiểm tra SmartCar của đơn #{bookingId}, sau đó chọn Đồng ý. Admin có thể tiếp tục xử lý phụ phí có căn cứ và hoàn tất kiểm tra.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã ghi nhận bạn đồng ý với bộ bằng chứng tình trạng xe trước–sau chuyến.";
        return RedirectToAction(nameof(Review), new { bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dispute(
        int bookingId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length < 10 || normalizedReason.Length > 1000)
        {
            TempData["ErrorMessage"] = "Vui lòng mô tả điểm không đồng ý từ 10 đến 1000 ký tự.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var booking = await _dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.BookingId == bookingId &&
                item.CustomerId == customerId,
                cancellationToken);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status != BookingStatus.PendingInspection)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở bước chờ kiểm tra sau trả xe.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var latestAction = await GetLatestReviewActionAsync(bookingId, cancellationToken);
        if (latestAction != ReturnEvidenceHelper.PendingAction)
        {
            TempData["ErrorMessage"] = "Biên bản này đã có phản hồi và không thể gửi phản hồi lần hai.";
            return RedirectToAction(nameof(Review), new { bookingId });
        }

        var createdAt = DateTime.UtcNow;
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = customerId,
            Action = ReturnEvidenceHelper.DisputedAction,
            EntityName = nameof(VehicleReturn),
            EntityId = bookingId.ToString(),
            Description =
                $"Khách không đồng ý với một phần hiện trạng/bằng chứng trả xe của đơn #{bookingId}. Lý do: {normalizedReason}",
            NewValues = JsonSerializer.Serialize(new
            {
                bookingId,
                Reason = normalizedReason,
                DisputedAt = createdAt
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = createdAt
        });

        await AddAdminNotificationsAsync(
            $"Khách yêu cầu xem xét hiện trạng trả xe|Booking:{bookingId}",
            $"Khách không đồng ý với một phần bộ ảnh/hiện trạng trả xe của đơn #{bookingId}. Lý do: {normalizedReason}. Không hoàn tất đơn hoặc quyết toán cọc cho đến khi phản hồi được xử lý.",
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Đã gửi yêu cầu xem xét. SmartCar sẽ đối chiếu lại ảnh trước–sau và căn cứ liên quan trước khi quyết toán.";
        return RedirectToAction(nameof(Review), new { bookingId });
    }

    private void SetCheckOutContext(
        Booking booking,
        CustomerVehicleEvidenceSnapshot? checkIn)
    {
        ViewBag.VehicleName = booking.Vehicle.VehicleName;
        ViewBag.LicensePlate = booking.Vehicle.LicensePlate;
        ViewBag.VehicleFuelType = booking.Vehicle.FuelType;
        ViewBag.ScheduledReturnDate = booking.ReturnDate;
        ViewBag.HandoverMileage = booking.Handover?.Mileage ?? 0;
        ViewBag.HandoverFuelLevel = booking.Handover?.FuelLevel ?? string.Empty;
        ViewBag.CustomerCheckIn = checkIn;
    }

    private async Task ValidateImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selected = images.Where(file => file.Length > 0).ToList();
        if (selected.Count < MinimumImages)
        {
            ModelState.AddModelError(
                nameof(CustomerVehicleCheckOutViewModel.EvidenceImages),
                "Bạn phải tự chụp đủ 6 ảnh check-out: chính diện, phía sau, bên trái, bên phải, ODO + nhiên liệu/pin và nội thất.");
            return;
        }

        if (selected.Count > MaximumImages)
        {
            ModelState.AddModelError(
                nameof(CustomerVehicleCheckOutViewModel.EvidenceImages),
                $"Bộ ảnh check-out tối đa {MaximumImages} ảnh.");
        }

        foreach (var image in selected)
        {
            var error = await ImageFileValidator.ValidateAsync(image, MaximumImageBytes, cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(
                    nameof(CustomerVehicleCheckOutViewModel.EvidenceImages),
                    $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<IReadOnlyList<string>> SaveImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/customer-checkout/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var paths = new List<string>();
        var index = 0;
        foreach (var image in images.Where(file => file.Length > 0))
        {
            index++;
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"{index:00}-{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(folder, fileName);

            await using var stream = System.IO.File.Create(fullPath);
            await image.CopyToAsync(stream, cancellationToken);
            paths.Add($"/{relativeFolder}/{fileName}");
        }

        return paths;
    }

    private async Task<string> ComputeEvidenceHashAsync(
        int bookingId,
        int mileage,
        string fuelLevel,
        bool hasIssue,
        string? note,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, $"BookingId={bookingId}");
        AppendText(hash, "Stage=CustomerCheckOut");
        AppendText(hash, $"Mileage={mileage}");
        AppendText(hash, $"FuelLevel={fuelLevel}");
        AppendText(hash, $"HasIssue={hasIssue}");
        AppendText(hash, $"Note={note?.Trim() ?? string.Empty}");

        foreach (var path in imagePaths)
        {
            AppendText(hash, $"ImagePath={path}");
            var fullPath = ResolveWebRootPath(path);
            await using var stream = System.IO.File.OpenRead(fullPath);
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, bytesRead);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0x0A });
    }

    private string ResolveWebRootPath(string relativePath)
    {
        var webRoot = Path.GetFullPath(_environment.WebRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(
            webRoot,
            relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Đường dẫn ảnh bằng chứng không hợp lệ.");
        }

        return fullPath;
    }

    private static (bool Succeeded, string? Value, string? Error) NormalizeFuelLevel(
        string vehicleFuelType,
        string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return (false, null, "Vui lòng ghi mức nhiên liệu/pin khi check-out.");
        }

        var value = rawValue.Trim();
        if (string.Equals(vehicleFuelType, "Điện", StringComparison.OrdinalIgnoreCase))
        {
            var numeric = value.TrimEnd('%').Trim();
            if (!int.TryParse(numeric, out var percent) || percent < 0 || percent > 100)
            {
                return (false, null, "Mức pin xe điện phải từ 0% đến 100%.");
            }
            return (true, $"{percent}%", null);
        }

        if (!FuelGaugeLevels.Contains(value))
        {
            return (false, null, "Mức nhiên liệu phải chọn theo vạch 0/8 đến 8/8 trên đồng hồ.");
        }

        return (true, value, null);
    }

    private Task<string?> GetLatestReviewActionAsync(
        int bookingId,
        CancellationToken cancellationToken) =>
        _dbContext.AuditLogs
            .AsNoTracking()
            .Where(log =>
                log.EntityName == nameof(VehicleReturn) &&
                log.EntityId == bookingId.ToString() &&
                ReturnEvidenceHelper.ReviewActions.Contains(log.Action))
            .OrderByDescending(log => log.CreatedAt)
            .Select(log => log.Action)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task AddAdminNotificationsAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        var adminRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Admin)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(adminRoleId))
        {
            return;
        }

        var adminIds = await _dbContext.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.RoleId == adminRoleId)
            .Select(userRole => userRole.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var adminId in adminIds)
        {
            _dbContext.Notifications.Add(new Notification
            {
                UserId = adminId,
                Title = title,
                Message = message
            });
        }
    }
}
