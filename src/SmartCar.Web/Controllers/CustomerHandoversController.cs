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
    private const int MaximumSignatureBytes = 2 * 1024 * 1024;
    private const string SignatureDataPrefix = "data:image/png;base64,";
    private const int MaximumCustomerImages = 6;
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

    [HttpGet]
    public async Task<IActionResult> Sign(
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

        if (!snapshot.CanCustomerSign)
        {
            TempData["SuccessMessage"] = "Biên bản không còn ở trạng thái chờ chữ ký. Kiểm tra trạng thái chuyến thuê trong chi tiết đơn.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var snapshotHash = await ComputeSnapshotHashAsync(snapshot, cancellationToken);
        SetSnapshot(snapshot);

        return View(new CustomerHandoverSignViewModel
        {
            BookingId = bookingId,
            SnapshotHash = snapshotHash
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sign(
        CustomerHandoverSignViewModel model,
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

        if (!snapshot.CanCustomerSign)
        {
            TempData["ErrorMessage"] = "Biên bản không còn ở trạng thái chờ chữ ký.";
            return RedirectToAction("Details", "Bookings", new { id = model.BookingId });
        }

        if (!model.Accepted)
        {
            ModelState.AddModelError(
                nameof(CustomerHandoverSignViewModel.Accepted),
                "Vui lòng xác nhận bạn đã xem bộ ảnh và thông tin trên biên bản trước khi ký.");
        }

        var currentSnapshotHash = await ComputeSnapshotHashAsync(snapshot, cancellationToken);
        if (!string.Equals(
                model.SnapshotHash?.Trim(),
                currentSnapshotHash,
                StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(
                string.Empty,
                "Nội dung biên bản đã thay đổi so với bản bạn vừa xem. Vui lòng tải lại biên bản và kiểm tra trước khi ký.");
            model.SnapshotHash = currentSnapshotHash;
        }

        var signatureResult = ValidateSignature(model.SignatureData);
        if (!signatureResult.Succeeded)
        {
            ModelState.AddModelError(
                nameof(CustomerHandoverSignViewModel.SignatureData),
                signatureResult.Error!);
        }

        await ValidateCustomerImagesAsync(model.CustomerImages, cancellationToken);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var signaturePath = await SaveSignatureAsync(
            model.BookingId,
            signatureResult.Bytes!,
            cancellationToken);
        var customerImagePaths = await SaveCustomerImagesAsync(
            model.BookingId,
            model.CustomerImages,
            cancellationToken);

        var evidenceHash = await ComputeCustomerEvidenceHashAsync(
            currentSnapshotHash,
            model.CustomerNote,
            customerImagePaths,
            cancellationToken);

        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 500)
        {
            userAgent = userAgent[..500];
        }

        var confirmResult = await _handoverService.ConfirmCustomerSignatureAsync(
            new ConfirmCustomerHandoverRequest(
                model.BookingId,
                customerId,
                currentSnapshotHash,
                signaturePath,
                model.CustomerNote,
                string.Join(';', customerImagePaths),
                evidenceHash,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent),
            cancellationToken);
        if (!confirmResult.Succeeded)
        {
            DeleteSavedFile(signaturePath);
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
            $"Khách đã ký biên bản|Booking:{model.BookingId}",
            $"Khách {snapshot.CustomerName} đã ký biên bản bàn giao đơn #{model.BookingId}" +
            (customerImagePaths.Count > 0 || !string.IsNullOrWhiteSpace(model.CustomerNote)
                ? $" và bổ sung {customerImagePaths.Count} ảnh/ghi chú hiện trạng."
                : ".") +
            " Chuyến thuê đã được kích hoạt; có thể hoàn tất việc giao chìa khóa.",
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] =
            "Bạn đã ký biên bản bàn giao thành công. Ảnh/ghi chú bổ sung (nếu có) đã được lưu cùng bằng chứng bàn giao và chuyến thuê chuyển sang trạng thái Đang thuê.";
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
            AppendText(hash, $"ImagePath={imagePath}");
            await AppendFileAsync(hash, imagePath, cancellationToken);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string> ComputeCustomerEvidenceHashAsync(
        string snapshotHash,
        string? customerNote,
        IReadOnlyList<string> customerImagePaths,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, $"SnapshotHash={snapshotHash}");
        AppendText(hash, $"CustomerNote={customerNote?.Trim() ?? string.Empty}");

        foreach (var imagePath in customerImagePaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            AppendText(hash, $"CustomerImagePath={imagePath}");
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

    private static (bool Succeeded, byte[]? Bytes, string? Error) ValidateSignature(string? signatureData)
    {
        if (string.IsNullOrWhiteSpace(signatureData) ||
            !signatureData.StartsWith(SignatureDataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "Vui lòng ký trực tiếp vào ô chữ ký trên biên bản.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(signatureData[SignatureDataPrefix.Length..]);
        }
        catch (FormatException)
        {
            return (false, null, "Dữ liệu chữ ký không hợp lệ. Vui lòng xóa và ký lại.");
        }

        if (bytes.Length < 100 || bytes.Length > MaximumSignatureBytes)
        {
            return (false, null, "Dữ liệu chữ ký không hợp lệ hoặc vượt quá giới hạn 2 MB.");
        }

        var isPng = bytes.Length >= 8 &&
                    bytes[0] == 0x89 &&
                    bytes[1] == 0x50 &&
                    bytes[2] == 0x4E &&
                    bytes[3] == 0x47 &&
                    bytes[4] == 0x0D &&
                    bytes[5] == 0x0A &&
                    bytes[6] == 0x1A &&
                    bytes[7] == 0x0A;
        if (!isPng)
        {
            return (false, null, "Chữ ký phải được tạo trực tiếp từ vùng ký của SmartCar.");
        }

        return (true, bytes, null);
    }

    private async Task ValidateCustomerImagesAsync(
        IReadOnlyCollection<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var selected = images.Where(file => file.Length > 0).ToList();
        if (selected.Count > MaximumCustomerImages)
        {
            ModelState.AddModelError(
                nameof(CustomerHandoverSignViewModel.CustomerImages),
                $"Bạn chỉ có thể bổ sung tối đa {MaximumCustomerImages} ảnh hiện trạng.");
        }

        foreach (var image in selected)
        {
            var error = await ImageFileValidator.ValidateAsync(image, MaximumImageBytes, cancellationToken);
            if (error is not null)
            {
                ModelState.AddModelError(
                    nameof(CustomerHandoverSignViewModel.CustomerImages),
                    $"{image.FileName}: {error}");
            }
        }
    }

    private async Task<string> SaveSignatureAsync(
        int bookingId,
        byte[] signatureBytes,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/handover-signatures/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var fileName = $"customer-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.png";
        var fullPath = Path.Combine(folder, fileName);
        await System.IO.File.WriteAllBytesAsync(fullPath, signatureBytes, cancellationToken);

        return $"/{relativeFolder}/{fileName}";
    }

    private async Task<IReadOnlyList<string>> SaveCustomerImagesAsync(
        int bookingId,
        IEnumerable<IFormFile> images,
        CancellationToken cancellationToken)
    {
        var relativeFolder = $"uploads/handover-customer/{bookingId}";
        var folder = Path.Combine(_environment.WebRootPath, relativeFolder);
        Directory.CreateDirectory(folder);

        var paths = new List<string>();
        foreach (var image in images.Where(file => file.Length > 0))
        {
            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"customer-{Guid.NewGuid():N}{extension}";
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
            throw new InvalidOperationException("Đường dẫn ảnh biên bản không hợp lệ.");
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
