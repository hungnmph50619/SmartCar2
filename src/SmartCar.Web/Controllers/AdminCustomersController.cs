using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Documents;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminCustomersController : Controller
{
    private static readonly BookingStatus[] ActiveBookingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDocumentService _documentService;
    private readonly ISecureDocumentStorage _documentStorage;
    private readonly IAuditService _auditService;

    public AdminCustomersController(
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IDocumentService documentService,
        ISecureDocumentStorage documentStorage,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _documentService = documentService;
        _documentStorage = documentStorage;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? query,
        string? profileStatus,
        string? accountStatus,
        CancellationToken cancellationToken)
    {
        var customerRoleId = await _dbContext.Roles
            .Where(role => role.Name == RoleNames.Customer)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(customerRoleId))
        {
            return View(new AdminCustomerIndexViewModel());
        }

        var customerQuery = _dbContext.Users
            .AsNoTracking()
            .Where(user => _dbContext.UserRoles.Any(item =>
                item.RoleId == customerRoleId && item.UserId == user.Id));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var keyword = query.Trim();
            customerQuery = customerQuery.Where(user =>
                user.FullName.Contains(keyword) ||
                (user.Email != null && user.Email.Contains(keyword)) ||
                (user.PhoneNumber != null && user.PhoneNumber.Contains(keyword)));
        }

        var customers = await customerQuery
            .OrderBy(user => user.FullName)
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
                user.PhoneNumber,
                user.IsActive,
                user.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var customerIds = customers.Select(customer => customer.Id).ToList();
        var documents = customerIds.Count == 0
            ? new List<CustomerDocument>()
            : await _dbContext.CustomerDocuments
                .AsNoTracking()
                .Where(document => customerIds.Contains(document.CustomerId))
                .ToListAsync(cancellationToken);
        var bookingSummaries = customerIds.Count == 0
            ? new List<CustomerBookingSummary>()
            : await _dbContext.Bookings
                .AsNoTracking()
                .Where(booking => customerIds.Contains(booking.CustomerId))
                .GroupBy(booking => booking.CustomerId)
                .Select(group => new CustomerBookingSummary(
                    group.Key,
                    group.Count(),
                    group.Any(booking => ActiveBookingStatuses.Contains(booking.Status))))
                .ToListAsync(cancellationToken);

        var documentsByCustomer = documents
            .GroupBy(document => document.CustomerId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<CustomerDocument>)group.ToList());
        var bookingByCustomer = bookingSummaries.ToDictionary(item => item.CustomerId);

        var allItems = customers.Select(customer =>
        {
            documentsByCustomer.TryGetValue(customer.Id, out var customerDocuments);
            bookingByCustomer.TryGetValue(customer.Id, out var bookingSummary);
            var state = ResolveProfileState(customerDocuments ?? Array.Empty<CustomerDocument>());

            return new AdminCustomerListItemViewModel
            {
                CustomerId = customer.Id,
                FullName = customer.FullName,
                Email = customer.Email ?? string.Empty,
                PhoneNumber = customer.PhoneNumber,
                IsActive = customer.IsActive,
                CreatedAt = customer.CreatedAt,
                ProfileStatusCode = state.Code,
                ProfileStatusText = state.Text,
                BookingCount = bookingSummary?.BookingCount ?? 0,
                HasActiveBooking = bookingSummary?.HasActiveBooking ?? false
            };
        }).ToList();

        var filteredItems = allItems.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(profileStatus))
        {
            filteredItems = filteredItems.Where(item =>
                string.Equals(item.ProfileStatusCode, profileStatus, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(accountStatus, "Active", StringComparison.OrdinalIgnoreCase))
        {
            filteredItems = filteredItems.Where(item => item.IsActive);
        }
        else if (string.Equals(accountStatus, "Locked", StringComparison.OrdinalIgnoreCase))
        {
            filteredItems = filteredItems.Where(item => !item.IsActive);
        }

        var model = new AdminCustomerIndexViewModel
        {
            Query = query,
            ProfileStatus = profileStatus,
            AccountStatus = accountStatus,
            TotalCustomers = allItems.Count,
            PendingVerificationCount = allItems.Count(item => item.ProfileStatusCode == "Pending"),
            NeedResubmissionCount = allItems.Count(item => item.ProfileStatusCode == "Rejected"),
            VerifiedCount = allItems.Count(item => item.ProfileStatusCode == "Verified"),
            Customers = filteredItems
                .OrderBy(item => GetProfilePriority(item.ProfileStatusCode))
                .ThenBy(item => item.FullName)
                .ToList()
        };

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        string id,
        string? tab,
        CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == id, cancellationToken);
        if (customer is null || !await IsCustomerAsync(id, cancellationToken))
        {
            return NotFound();
        }

        var documents = await _documentService.GetCustomerDocumentsAsync(id, cancellationToken);
        var bookings = await _dbContext.Bookings
            .AsNoTracking()
            .Include(booking => booking.Vehicle)
            .Where(booking => booking.CustomerId == id)
            .OrderByDescending(booking => booking.CreatedAt)
            .Select(booking => new AdminCustomerBookingItemViewModel
            {
                BookingId = booking.BookingId,
                VehicleName = booking.Vehicle.VehicleName,
                LicensePlate = booking.Vehicle.LicensePlate,
                PickupDate = booking.PickupDate,
                ReturnDate = booking.ReturnDate,
                Status = booking.Status,
                TotalAmount = booking.TotalAmount,
                CreatedAt = booking.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.Booking.CustomerId == id)
            .OrderByDescending(payment => payment.PaidAt ?? DateTime.MinValue)
            .Select(payment => new AdminCustomerPaymentItemViewModel
            {
                PaymentId = payment.PaymentId,
                BookingId = payment.BookingId,
                Type = payment.Type,
                Status = payment.Status,
                Amount = payment.Amount,
                Method = payment.Method,
                TransactionCode = payment.TransactionCode,
                PaidAt = payment.PaidAt
            })
            .ToListAsync(cancellationToken);

        var incidents = await _dbContext.VehicleIncidents
            .AsNoTracking()
            .Include(incident => incident.Vehicle)
            .Where(incident => incident.Booking != null && incident.Booking.CustomerId == id)
            .OrderByDescending(incident => incident.OccurredAt)
            .Select(incident => new AdminCustomerIncidentItemViewModel
            {
                IncidentId = incident.VehicleIncidentId,
                BookingId = incident.BookingId,
                VehicleName = incident.Vehicle.VehicleName,
                Type = incident.IncidentType,
                Status = incident.Status,
                OccurredAt = incident.OccurredAt,
                Description = incident.Description,
                CustomerLiabilityAmount = incident.CustomerLiabilityAmount
            })
            .ToListAsync(cancellationToken);

        var state = ResolveProfileState(documents);
        var activeTab = NormalizeTab(tab);
        var paidIn = payments
            .Where(payment =>
                payment.Status == PaymentStatus.Paid &&
                payment.Type is PaymentType.Rental or
                    PaymentType.Extension or
                    PaymentType.AdditionalCharge or
                    PaymentType.TrafficFine or
                    PaymentType.OverdueCompensationDebt)
            .Sum(payment => payment.Amount);
        var refunds = payments
            .Where(payment => payment.Type == PaymentType.Refund &&
                              (payment.Status == PaymentStatus.Paid || payment.Status == PaymentStatus.Refunded))
            .Sum(payment => payment.Amount);
        var outstandingCustomerObligation = payments
            .Where(payment => BookingWorkflowRules.BlocksNewRentalForOutstandingCustomerObligation(
                payment.Type,
                payment.Status,
                payment.Amount))
            .Sum(payment => payment.Amount);

        return View(new AdminCustomerDetailsViewModel
        {
            CustomerId = customer.Id,
            FullName = customer.FullName,
            Email = customer.Email ?? string.Empty,
            PhoneNumber = customer.PhoneNumber,
            Address = customer.Address,
            CreatedAt = customer.CreatedAt,
            IsActive = customer.IsActive,
            ActiveTab = activeTab,
            ProfileStatusCode = state.Code,
            ProfileStatusText = state.Text,
            CanRent = customer.IsActive &&
                state.Code == "Verified" &&
                outstandingCustomerObligation <= 0,
            CompletedBookingCount = bookings.Count(booking => booking.Status == BookingStatus.Completed),
            ActiveBookingCount = bookings.Count(booking => ActiveBookingStatuses.Contains(booking.Status)),
            CancelledBookingCount = bookings.Count(booking =>
                booking.Status is BookingStatus.Cancelled or BookingStatus.Rejected or BookingStatus.NoShow or BookingStatus.Expired),
            TotalPaidAmount = paidIn - refunds,
            PendingPaymentAmount = payments
                .Where(payment =>
                    payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation)
                .Sum(payment => payment.Amount),
            Documents = documents,
            Bookings = bookings,
            Payments = payments,
            Incidents = incidents
        });
    }

    [HttpGet]
    public async Task<IActionResult> ViewDocumentImage(
        int id,
        CancellationToken cancellationToken)
    {
        var document = await _documentService.GetDocumentAsync(id, cancellationToken);
        if (document is null ||
            !_documentStorage.TryResolve(document.ImagePath, out var fullPath, out var contentType))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return PhysicalFile(fullPath, contentType, enableRangeProcessing: false);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyDocument(
        int documentId,
        string customerId,
        CancellationToken cancellationToken)
    {
        var document = await _documentService.GetDocumentAsync(documentId, cancellationToken);
        if (document is null || document.CustomerId != customerId)
        {
            return NotFound();
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _documentService.VerifyAsync(documentId, adminId, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xác minh giấy tờ."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyCitizenId(
        string customerId,
        int frontDocumentId,
        int backDocumentId,
        CancellationToken cancellationToken)
    {
        var front = await _documentService.GetDocumentAsync(frontDocumentId, cancellationToken);
        var back = await _documentService.GetDocumentAsync(backDocumentId, cancellationToken);

        if (front is null || back is null ||
            front.CustomerId != customerId || back.CustomerId != customerId ||
            front.DocumentType != DocumentTypes.CitizenId ||
            back.DocumentType != DocumentTypes.CitizenIdBack)
        {
            return NotFound();
        }

        if (!front.HasRequiredData || !back.HasRequiredData)
        {
            TempData["ErrorMessage"] = "Hồ sơ CCCD chưa có đủ thông tin cần thiết để xác minh.";
            return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
        }

        if (front.Status != DocumentStatus.Pending || back.Status != DocumentStatus.Pending)
        {
            TempData["ErrorMessage"] = "Cả hai mặt CCCD phải ở trạng thái chờ xác minh.";
            return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var frontResult = await _documentService.VerifyAsync(frontDocumentId, adminId, cancellationToken);
        if (!frontResult.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", frontResult.Errors);
            return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
        }

        var backResult = await _documentService.VerifyAsync(backDocumentId, adminId, cancellationToken);
        TempData[backResult.Succeeded ? "SuccessMessage" : "ErrorMessage"] = backResult.Succeeded
            ? "Đã xác minh danh tính CCCD gồm thông tin, mặt trước và mặt sau."
            : string.Join("; ", backResult.Errors);

        return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestCitizenIdResubmission(
        string customerId,
        int frontDocumentId,
        int backDocumentId,
        string reason,
        string? additionalNote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["ErrorMessage"] = "Vui lòng chọn lý do yêu cầu cập nhật CCCD.";
            return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
        }

        var front = await _documentService.GetDocumentAsync(frontDocumentId, cancellationToken);
        var back = await _documentService.GetDocumentAsync(backDocumentId, cancellationToken);
        if (front is null || back is null ||
            front.CustomerId != customerId || back.CustomerId != customerId ||
            front.DocumentType != DocumentTypes.CitizenId ||
            back.DocumentType != DocumentTypes.CitizenIdBack)
        {
            return NotFound();
        }

        var fullReason = reason.Trim();
        if (!string.IsNullOrWhiteSpace(additionalNote))
        {
            fullReason = $"{fullReason}. {additionalNote.Trim()}";
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var errors = new List<string>();

        foreach (var document in new[] { front, back })
        {
            if (document.Status is not (DocumentStatus.Pending or DocumentStatus.Verified))
            {
                continue;
            }

            var result = await _documentService.RejectAsync(
                document.CustomerDocumentId,
                adminId,
                fullReason,
                cancellationToken);
            if (!result.Succeeded)
            {
                errors.AddRange(result.Errors);
            }
        }

        if (errors.Count > 0)
        {
            TempData["ErrorMessage"] = string.Join("; ", errors.Distinct());
        }
        else
        {
            TempData["SuccessMessage"] =
                "Đã yêu cầu khách hàng cập nhật lại toàn bộ thông tin và hai ảnh CCCD.";
        }

        return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDocumentResubmission(
        RequestDocumentResubmissionViewModel model,
        string customerId,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Vui lòng chọn lý do yêu cầu khách hàng gửi lại giấy tờ.";
            return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
        }

        var document = await _documentService.GetDocumentAsync(model.DocumentId, cancellationToken);
        if (document is null || document.CustomerId != customerId)
        {
            return NotFound();
        }

        var reason = model.Reason.Trim();
        if (!string.IsNullOrWhiteSpace(model.AdditionalNote))
        {
            reason = $"{reason}. {model.AdditionalNote.Trim()}";
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _documentService.RejectAsync(
            model.DocumentId,
            adminId,
            reason,
            cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã yêu cầu khách hàng gửi lại giấy tờ. Khách hàng đã nhận được thông báo."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendDocumentReminder(
        string customerId,
        string? documentType,
        CancellationToken cancellationToken)
    {
        if (!await IsCustomerAsync(customerId, cancellationToken))
        {
            return NotFound();
        }

        var subject = string.IsNullOrWhiteSpace(documentType)
            ? "CCCD và GPLX"
            : documentType.Trim();
        _dbContext.Notifications.Add(new Notification
        {
            UserId = customerId,
            Title = "Vui lòng hoàn thiện hồ sơ thuê xe",
            Message = $"Bạn cần gửi hoặc cập nhật {subject} trong Hồ sơ cá nhân để tiếp tục sử dụng dịch vụ thuê xe."
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            "SendDocumentReminder",
            "CustomerDocument",
            customerId,
            $"Gửi lời nhắc hoàn thiện {subject} cho khách hàng {customerId}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = "Đã gửi lời nhắc cho khách hàng.";
        return RedirectToAction(nameof(Details), new { id = customerId, tab = "documents" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAccountStatus(
        CustomerAccountStatusViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập lý do thay đổi trạng thái tài khoản.";
            return RedirectToAction(nameof(Details), new { id = model.CustomerId });
        }

        var customer = await _userManager.FindByIdAsync(model.CustomerId);
        if (customer is null || !await IsCustomerAsync(model.CustomerId, cancellationToken))
        {
            return NotFound();
        }

        customer.IsActive = model.Activate;
        customer.LockoutEnabled = true;
        customer.LockoutEnd = model.Activate ? null : DateTimeOffset.MaxValue;
        var result = await _userManager.UpdateAsync(customer);
        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors.Select(error => error.Description));
            return RedirectToAction(nameof(Details), new { id = model.CustomerId });
        }

        if (!model.Activate)
        {
            await _userManager.UpdateSecurityStampAsync(customer);
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _auditService.WriteAsync(
            adminId,
            model.Activate ? "UnlockCustomer" : "LockCustomer",
            "UserProfile",
            model.CustomerId,
            $"{(model.Activate ? "Mở khóa" : "Khóa")} tài khoản khách hàng. Lý do: {model.Reason.Trim()}",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = model.Activate
            ? "Đã mở khóa tài khoản khách hàng."
            : "Đã khóa tài khoản khách hàng.";
        return RedirectToAction(nameof(Details), new { id = model.CustomerId });
    }

    private async Task<bool> IsCustomerAsync(
        string customerId,
        CancellationToken cancellationToken)
    {
        return await (
            from userRole in _dbContext.UserRoles
            join role in _dbContext.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == customerId && role.Name == RoleNames.Customer
            select userRole.UserId)
            .AnyAsync(cancellationToken);
    }

    private static ProfileState ResolveProfileState(
        IReadOnlyCollection<CustomerDocument> documents)
    {
        var requiredDocuments = DocumentTypes.RequiredForRental
            .Select(type => documents.FirstOrDefault(document => document.DocumentType == type))
            .ToList();

        if (requiredDocuments.Any(document => document is null))
        {
            return new ProfileState("Missing", "Chưa hoàn tất");
        }

        if (requiredDocuments.Any(document => document!.Status == DocumentStatus.Rejected))
        {
            return new ProfileState("Rejected", "Cần gửi lại");
        }

        if (requiredDocuments.Any(document => document!.Status == DocumentStatus.Pending))
        {
            return new ProfileState("Pending", "Chờ xác minh");
        }

        var citizenFront = requiredDocuments.First(document =>
            document!.DocumentType == DocumentTypes.CitizenId)!;
        var drivingLicenseFront = requiredDocuments.First(document =>
            document!.DocumentType == DocumentTypes.DrivingLicense)!;

        if (!citizenFront.ExpiryDate.HasValue ||
            citizenFront.ExpiryDate.Value.Date < DateTime.Today ||
            !drivingLicenseFront.ExpiryDate.HasValue ||
            drivingLicenseFront.ExpiryDate.Value.Date < DateTime.Today)
        {
            return new ProfileState("Expired", "Giấy tờ hết hạn");
        }

        return requiredDocuments.All(document => document!.Status == DocumentStatus.Verified)
            ? new ProfileState("Verified", "Đã xác minh")
            : new ProfileState("Missing", "Chưa hoàn tất");
    }

    private static ProfileState ResolveProfileState(
        IReadOnlyCollection<DocumentDto> documents)
    {
        var citizenFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
        var citizenBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenIdBack);
        var drivingLicenseFront = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);
        var drivingLicenseBack = documents.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicenseBack);

        var requiredDocuments = new[]
        {
            citizenFront,
            citizenBack,
            drivingLicenseFront,
            drivingLicenseBack
        };

        if (requiredDocuments.Any(item => item is null))
        {
            return new ProfileState("Missing", "Chưa hoàn tất");
        }

        if (requiredDocuments.Any(item => !item!.HasRequiredData))
        {
            return new ProfileState("Missing", "Thiếu thông tin xác minh");
        }

        if (requiredDocuments.Any(item => item!.Status == DocumentStatus.Rejected))
        {
            return new ProfileState("Rejected", "Cần gửi lại");
        }

        if (requiredDocuments.Any(item => item!.Status == DocumentStatus.Pending))
        {
            return new ProfileState("Pending", "Chờ xác minh");
        }

        var cccdExpired = !citizenFront!.ExpiryDate.HasValue ||
                          citizenFront.ExpiryDate.Value.Date < DateTime.Today;
        var licenseExpired = !drivingLicenseFront!.ExpiryDate.HasValue ||
                             drivingLicenseFront.ExpiryDate.Value.Date < DateTime.Today;
        if (cccdExpired || licenseExpired)
        {
            return new ProfileState("Expired", "Giấy tờ hết hạn");
        }

        return requiredDocuments.All(item => item!.Status == DocumentStatus.Verified)
            ? new ProfileState("Verified", "Đã xác minh")
            : new ProfileState("Missing", "Chưa hoàn tất");
    }

    private static int GetProfilePriority(string code) => code switch
    {
        "Pending" => 0,
        "Rejected" => 1,
        "Expired" => 2,
        "Missing" => 3,
        "Verified" => 4,
        _ => 5
    };

    private static string NormalizeTab(string? tab) => tab?.ToLowerInvariant() switch
    {
        "documents" => "documents",
        "bookings" => "bookings",
        "payments" => "payments",
        "incidents" => "incidents",
        _ => "overview"
    };

    private sealed record ProfileState(string Code, string Text);
    private sealed record CustomerBookingSummary(
        string CustomerId,
        int BookingCount,
        bool HasActiveBooking);
}
