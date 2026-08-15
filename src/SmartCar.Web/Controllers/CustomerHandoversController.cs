using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Handovers;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class CustomerHandoversController : Controller
{
    private const int MinimumCustomerImages = 6;
    private const int MaximumCustomerImages = 12;
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    private readonly IHandoverService _handoverService;
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;

    public CustomerHandoversController(
        IHandoverService handoverService,
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment)
    {
        _handoverService = handoverService;
        _dbContext = dbContext;
        _environment = environment;
    }

    // Giữ route cũ để bookmark/link cũ không lỗi sau khi chuyển từ ký canvas sang check-in ảnh.
    [HttpGet]
    public IActionResult Sign(int bookingId) =>
        RedirectToAction(nameof(CheckIn), new { bookingId });

    [HttpGet]
    public async Task<IActionResult> CheckIn(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var snapshot = await _handoverService.GetCustomerSnapshotAsync(
            bookingId,
            customerId,
            cancellationToken);
        if (snapshot is null)
        {
            return NotFound();
        }

        if (!snapshot.CanCustomerCheckIn)
        {
            TempData["SuccessMessage"] = "Đơn không còn ở trạng thái chờ check-in nhận xe. Kiểm tra trạng thái chuyến thuê trong chi tiết đơn.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var snapshotHash = await ComputeSnapshotHashAsync(snapshot, cancellationToken);
        SetSnapshot(snapshot);

        return View(new CustomerHandoverCheckInViewModel
        {
            BookingId = bookingId,
            SnapshotHash = snapshotHash
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckIn(
        CustomerHandoverCheckInViewModel model,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var snapshot = await _handoverService.GetCustomerSnapshotAsync(
            model.BookingId,
            customerId,
            cancellationToken);
        if (snapshot is null)
        {
            return NotFound();
        }

        SetSnapshot(snapshot);

        if (!snapshot.CanCustomerCheckIn)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở trạng thái chờ check-in nhận xe.";
            return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
        }

        if (!model.Accepted)
        {
            ModelState.AddModelError(
                nameof(CustomerHandoverCheckInViewModel.Accepted),
                "Vui lòng xác nhận bạn đã tự chụp bộ ảnh check-in và đã khai hư hỏng có sẵn nếu có.");
        }

        if (model.HasPreExistingIssue && string.IsNullOrWhiteSpace(model.CustomerNote))
        {
            ModelState.AddModelError(
                nameof(CustomerHandoverCheckInViewModel.CustomerNote),
                "Vui lòng mô tả rõ vị trí/tình trạng hư hỏng đã có sẵn trước khi nhận xe.");
        }

        var currentSnapshotHash = await ComputeSnapshotHashAsync(snapshot, cancellationToken);
        if (!string.Equals(
                model.SnapshotHash?.Trim(),
                currentSnapshotHash,
                StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(
                string.Empty,
                "Hồ sơ SmartCar đã thay đổi so với bản bạn vừa xem. Vui lòng tải lại và kiểm tra trước khi check-in.");
            model.SnapshotHash = currentSnapshotHash;
        }

        await ValidateCustomerImagesAsync(model.EvidenceImages, cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var customerImagePaths = await SaveCustomerImagesAsync(
            model.BookingId,
            model.EvidenceImages,
            cancellationToken);

        var evidenceHash = await ComputeCustomerEvidenceHashAsync(
            currentSnapshotHash,
            model.HasPreExistingIssue,
            model.CustomerNote,
            customerImagePaths,
            cancellationToken);

        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 500)
        {
            userAgent = userAgent[..500];
        }

        var confirmResult = await _handoverService.ConfirmCustomerCheckInAsync(
            new ConfirmCustomerHandoverRequest(
                model.BookingId,
                customerId,
                currentSnapshotHash,
                model.HasPreExistingIssue,
                model.CustomerNote,
                string.Join(';', customerImagePaths),
                evidenceHash,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent),
            cancellationToken);
        if (!confirmResult.Succeeded)
        {
            foreach (var path in customerImagePaths)
            {
                DeleteSavedFile(path);
            }

            foreach (var error in confirmResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        await AddAdminNotificationsAsync(
            $"Khách đã check-in xe|Booking:{model.BookingId}",
            $"Khách {snapshot.CustomerName} đã tự chụp {customerImagePaths.Count} ảnh check-in cho đơn #{model.BookingId}; " +
            $"khai hư hỏng có sẵn: {(model.HasPreExistingIssue ? "Có" : "Không")}. Bộ bằng chứng đã được khóa và chuyến thuê đã bắt đầu.",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] =
            "Check-in hoàn tất. Bộ ảnh do chính tài khoản của bạn tạo đã được khóa làm mốc tình trạng trước chuyến; chuyến thuê chuyển sang Đang thuê.";
        return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
    }

    private void SetSnapshot(CustomerHandoverSnapshotDto snapshot)
    {
        ViewBag.HandoverSnapshot = snapshot;
    }

    private async Task<string> ComputeSnapshotHashAsync(
        CustomerHandoverSnapshotDto snapshot,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendText(hash, $"BookingId={snapshot.BookingId}");
        AppendText(hash, $"Customer={snapshot.CustomerName}");
        AppendText(hash, $"Vehicle={snapshot.VehicleName}");
        AppendText(hash, $"LicensePlate={snapshot.LicensePlate}");
        AppendText(hash, $"PickupDate={snapshot.PickupDate:O}");
        AppendText(hash, $"ReturnDate={snapshot.ReturnDate:O}");
        AppendText(hash, $"HandoverAt={snapshot.HandoverAt:O}");
        AppendText(hash, $"Mileage={snapshot.Mileage}");
        AppendText(hash, $"FuelLevel={snapshot.FuelLevel}");
        AppendText(hash, $"Accessories={snapshot.Accessories ?? string.Empty}");
        AppendText(hash, $"Notes={snapshot.Notes ?? string.Empty}");

        foreach (var imagePath in snapshot.ImagePaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            AppendText(hash, $"AdminImagePath={imagePath}");
            await AppendFileAsync(hash, imagePath, cancellationToken);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string> ComputeCustomerEvidenceHashAsync(
        string snapshotHash,
        bool hasPreExistingIssue,
        string? customerNote,
        IReadOnlyList<string> customerImagePaths,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, $"AdminSnapshotHash={snapshotHash}");
        AppendText(hash, $"HasPreExistingIssue={hasPreExistingIssue}");
        AppendText(hash, $"CustomerNote={customerNote?.Trim() ?? string.Empty}");

        foreach (var imagePath in customerImagePaths)
        {
            AppendText(hash, $"CustomerCheckInImagePath={imagePath}");
            await AppendFileAsync(hash, imagePath, cancellationToken);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task AppendFileAsync(
        IncrementalHash hash,
        string imagePath,
        CancellationToken cancellationToken)
    {
        var fullPath = ResolveWebRootPath(imagePath);
        if (!System.IO.File.Exists(fullPath))
        {
            AppendText(hash, "ImageMissing=true");
            return;
        }

        await using var stream = System.IO.File.OpenRead(fullPath);
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0x0A });
    }

    private async Task ValidateCustomerImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selected = images.Where(file => file.Length > 0).ToList();
        if (selected.Count < MinimumCustomerImages)
        {
            ModelState.AddModelError(
                nameof(CustomerHandoverCheckInViewModel.EvidenceImages),
                "Bạn phải tự chụp đủ 6 ảnh check-in: chính diện, phía sau, bên trái, bên phải, ODO + nhiên liệu/pin và nội thất.");
            return;
        }

        if (selected.Count > MaximumCustomerImages)
        {
            ModelState.AddModelError(
                nameof(CustomerHandoverCheckInViewModel.EvidenceImages),
                $"Bộ ảnh check-in tối đa {MaximumCustomerImages} ảnh.");
        }

        foreach (var image in selected)
        {
            var error = await ImageFileValidator.ValidateAsync(image, MaximumImageBytes, cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(
                    nameof(CustomerHandoverCheckInViewModel.EvidenceImages),
                    $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<IReadOnlyList<string>> SaveCustomerImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/customer-checkin/{bookingId}";
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

    private void DeleteSavedFile(string relativePath)
    {
        var fullPath = ResolveWebRootPath(relativePath);
        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }
    }

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
