using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize]
[Route("Account")]
public class CustomerAccountController : Controller
{
    private static readonly HashSet<string> AllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "profile",
        "documents",
        "bookings",
        "address",
        "payments",
        "notifications",
        "support",
        "password"
    };

    private static readonly IReadOnlyDictionary<string, string> DocumentLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CCCD_FRONT"] = "CCCD mặt trước",
            ["CCCD_BACK"] = "CCCD mặt sau",
            ["DRIVER_LICENSE"] = "Giấy phép lái xe"
        };

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;

    public CustomerAccountController(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _environment = environment;
    }

    [HttpGet("Profile")]
    public async Task<IActionResult> Profile(
        string section = "profile",
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var activeSection = AllowedSections.Contains(section) ? section.ToLowerInvariant() : "profile";

        var documents = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document => document.CustomerId == user.Id)
            .OrderByDescending(document => document.CreatedAt)
            .ToListAsync(cancellationToken);

        var bookings = await _dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Vehicle)
                .ThenInclude(vehicle => vehicle.Images)
            .Include(booking => booking.Payment)
            .Where(booking => booking.CustomerId == user.Id)
            .OrderByDescending(booking => booking.CreatedAt)
            .ToListAsync(cancellationToken);

        var notifications = await _dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.UserId == user.Id)
            .OrderByDescending(notification => notification.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var bookingItems = bookings.Select(MapBooking).ToList();
        var paymentItems = bookings
            .Where(booking => booking.Payment is not null)
            .Select(booking => MapPayment(booking, booking.Payment!))
            .OrderByDescending(payment => payment.PaidAt ?? DateTime.MinValue)
            .ToList();

        var model = new CustomerAccountDashboardViewModel
        {
            ActiveSection = activeSection,
            Profile = new CustomerAccountProfileViewModel
            {
                FullName = user.FullName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                Address = user.Address,
                AvatarPath = user.AvatarPath,
                CreatedAt = user.CreatedAt
            },
            Documents = BuildDocumentItems(documents),
            Bookings = bookingItems,
            Payments = paymentItems,
            Notifications = notifications.Select(notification => new CustomerNotificationItemViewModel
            {
                NotificationId = notification.NotificationId,
                Title = notification.Title,
                Message = notification.Message,
                IsRead = notification.IsRead,
                CreatedAt = notification.CreatedAt
            }).ToList(),
            CompletedBookingCount = bookings.Count(booking => booking.Status == BookingStatus.Completed),
            UnreadNotificationCount = notifications.Count(notification => !notification.IsRead),
            VerifiedDocumentCount = documents.Count(document => document.Status == DocumentStatus.Verified)
        };

        return View("~/Views/Account/Profile.cshtml", model);
    }

    [HttpPost("UpdateProfile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfile(
        UpdateCustomerProfileViewModel model,
        CancellationToken cancellationToken = default)
    {
        var returnSection = AllowedSections.Contains(model.ReturnSection)
            ? model.ReturnSection.ToLowerInvariant()
            : "profile";

        if (!ModelState.IsValid)
        {
            TempData["AccountErrorMessage"] = string.Join(
                " ",
                ModelState.Values
                    .SelectMany(value => value.Errors)
                    .Select(error => error.ErrorMessage)
                    .Where(message => !string.IsNullOrWhiteSpace(message)));

            return RedirectToAction(nameof(Profile), new { section = returnSection });
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var phoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber)
            ? null
            : model.PhoneNumber.Trim();

        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var phoneExists = await _userManager.Users
                .AnyAsync(
                    account => account.Id != user.Id && account.PhoneNumber == phoneNumber,
                    cancellationToken);

            if (phoneExists)
            {
                TempData["AccountErrorMessage"] = "Số điện thoại này đã được sử dụng bởi tài khoản khác.";
                return RedirectToAction(nameof(Profile), new { section = returnSection });
            }
        }

        user.FullName = model.FullName.Trim();
        user.PhoneNumber = phoneNumber;
        user.Address = string.IsNullOrWhiteSpace(model.Address) ? null : model.Address.Trim();

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            TempData["AccountErrorMessage"] = string.Join(
                " ",
                result.Errors.Select(error => TranslateIdentityError(error.Description)));

            return RedirectToAction(nameof(Profile), new { section = returnSection });
        }

        TempData["AccountSuccessMessage"] = returnSection == "address"
            ? "Đã cập nhật địa chỉ giao nhận mặc định."
            : "Đã cập nhật thông tin tài khoản.";

        return RedirectToAction(nameof(Profile), new { section = returnSection });
    }

    [HttpPost("UploadDocument")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_500_000)]
    public async Task<IActionResult> UploadDocument(
        string documentType,
        string documentNumber,
        DateTime? expiryDate,
        IFormFile? imageFile,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!DocumentLabels.ContainsKey(documentType))
        {
            TempData["AccountErrorMessage"] = "Loại giấy tờ không hợp lệ.";
            return RedirectToAction(nameof(Profile), new { section = "documents" });
        }

        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            TempData["AccountErrorMessage"] = "Vui lòng nhập số giấy tờ.";
            return RedirectToAction(nameof(Profile), new { section = "documents" });
        }

        if (imageFile is null || imageFile.Length == 0)
        {
            TempData["AccountErrorMessage"] = "Vui lòng chọn ảnh giấy tờ cần tải lên.";
            return RedirectToAction(nameof(Profile), new { section = "documents" });
        }

        if (imageFile.Length > 5_000_000)
        {
            TempData["AccountErrorMessage"] = "Dung lượng ảnh không được vượt quá 5 MB.";
            return RedirectToAction(nameof(Profile), new { section = "documents" });
        }

        var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        if (!allowedExtensions.Contains(extension))
        {
            TempData["AccountErrorMessage"] = "Chỉ chấp nhận ảnh JPG, JPEG, PNG hoặc WEBP.";
            return RedirectToAction(nameof(Profile), new { section = "documents" });
        }

        var webRootPath = _environment.WebRootPath
            ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var relativeFolder = Path.Combine("uploads", "customer-documents", user.Id);
        var absoluteFolder = Path.Combine(webRootPath, relativeFolder);
        Directory.CreateDirectory(absoluteFolder);

        var fileName = $"{documentType.ToLowerInvariant()}-{Guid.NewGuid():N}{extension}";
        var absolutePath = Path.Combine(absoluteFolder, fileName);

        await using (var stream = System.IO.File.Create(absolutePath))
        {
            await imageFile.CopyToAsync(stream, cancellationToken);
        }

        var imagePath = "/" + Path.Combine(relativeFolder, fileName).Replace('\\', '/');
        var document = await _dbContext.CustomerDocuments
            .FirstOrDefaultAsync(
                item => item.CustomerId == user.Id && item.DocumentType == documentType,
                cancellationToken);

        if (document is null)
        {
            document = new CustomerDocument
            {
                CustomerId = user.Id,
                DocumentType = documentType,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.CustomerDocuments.Add(document);
        }
        else if (!string.IsNullOrWhiteSpace(document.ImagePath))
        {
            TryDeleteOldDocument(webRootPath, document.ImagePath);
        }

        document.DocumentNumber = documentNumber.Trim();
        document.ExpiryDate = expiryDate;
        document.ImagePath = imagePath;
        document.Status = DocumentStatus.Pending;
        document.RejectionReason = null;

        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AccountSuccessMessage"] =
            $"Đã gửi {DocumentLabels[documentType]} để SmartCar kiểm tra.";

        return RedirectToAction(nameof(Profile), new { section = "documents" });
    }

    [HttpPost("ChangePassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangeCustomerPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["AccountErrorMessage"] = string.Join(
                " ",
                ModelState.Values
                    .SelectMany(value => value.Errors)
                    .Select(error => error.ErrorMessage)
                    .Where(message => !string.IsNullOrWhiteSpace(message)));

            return RedirectToAction(nameof(Profile), new { section = "password" });
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var result = await _userManager.ChangePasswordAsync(
            user,
            model.CurrentPassword,
            model.NewPassword);

        if (!result.Succeeded)
        {
            TempData["AccountErrorMessage"] = string.Join(
                " ",
                result.Errors.Select(error => TranslateIdentityError(error.Description)));

            return RedirectToAction(nameof(Profile), new { section = "password" });
        }

        await _userManager.UpdateSecurityStampAsync(user);
        TempData["AccountSuccessMessage"] = "Đổi mật khẩu thành công. Vui lòng đăng nhập lại.";
        return RedirectToAction("LogoutAndLogin", "Account");
    }

    [HttpPost("MarkNotificationRead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNotificationRead(
        int id,
        CancellationToken cancellationToken = default)
    {
        var userId = _userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var notification = await _dbContext.Notifications
            .FirstOrDefaultAsync(
                item => item.NotificationId == id && item.UserId == userId,
                cancellationToken);

        if (notification is not null && !notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return RedirectToAction(nameof(Profile), new { section = "notifications" });
    }

    [HttpPost("MarkAllNotificationsRead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllNotificationsRead(
        CancellationToken cancellationToken = default)
    {
        var userId = _userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var notifications = await _dbContext.Notifications
            .Where(item => item.UserId == userId && !item.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var notification in notifications)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
        }

        if (notifications.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        TempData["AccountSuccessMessage"] = "Đã đánh dấu tất cả thông báo là đã đọc.";
        return RedirectToAction(nameof(Profile), new { section = "notifications" });
    }

    private static IReadOnlyList<CustomerDocumentItemViewModel> BuildDocumentItems(
        IReadOnlyCollection<CustomerDocument> documents)
    {
        return DocumentLabels.Select(pair =>
        {
            var document = documents.FirstOrDefault(item =>
                string.Equals(item.DocumentType, pair.Key, StringComparison.OrdinalIgnoreCase));

            var status = GetDocumentStatus(document?.Status);
            return new CustomerDocumentItemViewModel
            {
                DocumentType = pair.Key,
                TypeLabel = pair.Value,
                DocumentNumber = document?.DocumentNumber,
                ExpiryDate = document?.ExpiryDate,
                ImagePath = document?.ImagePath,
                StatusText = document is null ? "Chưa cập nhật" : status.Text,
                StatusClass = document is null ? "neutral" : status.CssClass,
                RejectionReason = document?.RejectionReason
            };
        }).ToList();
    }

    private static CustomerBookingItemViewModel MapBooking(Booking booking)
    {
        var status = GetBookingStatus(booking.Status);
        var paymentStatus = booking.Payment is null
            ? "Chưa thanh toán"
            : GetPaymentStatus(booking.Payment.Status).Text;

        return new CustomerBookingItemViewModel
        {
            BookingId = booking.BookingId,
            VehicleName = booking.Vehicle.VehicleName,
            LicensePlate = booking.Vehicle.LicensePlate,
            ImagePath = booking.Vehicle.Images.FirstOrDefault()?.ImagePath,
            PickupDate = booking.PickupDate,
            ReturnDate = booking.ReturnDate,
            TotalAmount = booking.TotalAmount,
            StatusText = status.Text,
            StatusClass = status.CssClass,
            NextActionText = status.NextAction,
            ProgressPercent = status.Progress,
            CancelReason = booking.CancelReason,
            HasPayment = booking.Payment is not null,
            PaymentStatusText = paymentStatus
        };
    }

    private static CustomerPaymentItemViewModel MapPayment(Booking booking, Payment payment)
    {
        var status = GetPaymentStatus(payment.Status);
        return new CustomerPaymentItemViewModel
        {
            PaymentId = payment.PaymentId,
            BookingId = booking.BookingId,
            VehicleName = booking.Vehicle.VehicleName,
            Amount = payment.Amount,
            Method = payment.Method,
            StatusText = status.Text,
            StatusClass = status.CssClass,
            PaidAt = payment.PaidAt,
            TransactionCode = payment.TransactionCode
        };
    }

    private static (string Text, string CssClass) GetDocumentStatus(DocumentStatus? status) => status switch
    {
        DocumentStatus.Pending => ("Chờ SmartCar kiểm tra", "warning"),
        DocumentStatus.Verified => ("Đã xác minh", "success"),
        DocumentStatus.Rejected => ("Cần bổ sung", "danger"),
        _ => ("Chưa cập nhật", "neutral")
    };

    private static (string Text, string CssClass, string NextAction, int Progress) GetBookingStatus(
        BookingStatus status) => status switch
    {
        BookingStatus.PendingConfirmation => (
            "Chờ SmartCar xác nhận",
            "warning",
            "SmartCar đang kiểm tra lịch và tình trạng xe.",
            15),
        BookingStatus.Rejected => (
            "SmartCar từ chối",
            "danger",
            "Xem lý do từ chối và chọn xe khác phù hợp hơn.",
            15),
        BookingStatus.PendingPayment => (
            "Chờ thanh toán cọc",
            "warning",
            "Thanh toán tiền cọc để giữ lịch xe.",
            35),
        BookingStatus.Paid => (
            "Đã thanh toán cọc",
            "info",
            "SmartCar đang chuẩn bị xe cho lịch nhận.",
            50),
        BookingStatus.ReadyForPickup => (
            "Sắp nhận xe",
            "primary",
            "Mang theo CCCD và giấy phép lái xe bản gốc khi nhận xe.",
            65),
        BookingStatus.Rented => (
            "Đang thuê",
            "success",
            "Sử dụng xe đúng thỏa thuận và liên hệ SmartCar khi cần hỗ trợ.",
            80),
        BookingStatus.PendingInspection => (
            "Chờ kiểm tra trả xe",
            "warning",
            "SmartCar đang kiểm tra xe và các khoản phát sinh nếu có.",
            92),
        BookingStatus.Completed => (
            "Hoàn thành",
            "success",
            "Đơn thuê đã hoàn tất.",
            100),
        BookingStatus.Cancelled => (
            "Đã hủy",
            "danger",
            "Đơn thuê không còn hiệu lực.",
            0),
        _ => ("Đang xử lý", "neutral", "Vui lòng theo dõi trạng thái đơn.", 10)
    };

    private static (string Text, string CssClass) GetPaymentStatus(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => ("Chờ thanh toán", "warning"),
        PaymentStatus.Paid => ("Đã thanh toán", "success"),
        PaymentStatus.Failed => ("Thanh toán thất bại", "danger"),
        PaymentStatus.Refunded => ("Đã hoàn tiền", "info"),
        _ => ("Chưa xác định", "neutral")
    };

    private static string TranslateIdentityError(string error)
    {
        if (error.Contains("Incorrect password", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu hiện tại không chính xác.";
        }

        if (error.Contains("at least one lowercase", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu mới phải có ít nhất một chữ thường.";
        }

        if (error.Contains("at least one uppercase", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu mới phải có ít nhất một chữ hoa.";
        }

        if (error.Contains("at least one digit", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu mới phải có ít nhất một chữ số.";
        }

        if (error.Contains("must be at least", StringComparison.OrdinalIgnoreCase))
        {
            return "Mật khẩu mới chưa đủ độ dài tối thiểu.";
        }

        return "Không thể cập nhật thông tin. Vui lòng kiểm tra lại và thử lại.";
    }

    private static void TryDeleteOldDocument(string webRootPath, string imagePath)
    {
        try
        {
            var relativePath = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var absolutePath = Path.GetFullPath(Path.Combine(webRootPath, relativePath));
            var rootPath = Path.GetFullPath(webRootPath);

            if (absolutePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(absolutePath))
            {
                System.IO.File.Delete(absolutePath);
            }
        }
        catch
        {
            // Không chặn việc tải ảnh mới nếu ảnh cũ không thể xóa.
        }
    }
}
