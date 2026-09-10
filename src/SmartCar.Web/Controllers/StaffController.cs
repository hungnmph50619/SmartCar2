using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff + "," + RoleNames.Admin)]
public sealed class StaffController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private static readonly BookingStatus[] OperationalStatuses =
    {
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection,
        BookingStatus.AwaitingRefund,
        BookingStatus.Completed
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IBookingService _bookingService;
    private readonly IAuditService _auditService;
    private readonly IUserBankAccountService _bankAccountService;

    public StaffController(
        ApplicationDbContext dbContext,
        IBookingService bookingService,
        IAuditService auditService,
        IUserBankAccountService bankAccountService)
    {
        _dbContext = dbContext;
        _bookingService = bookingService;
        _auditService = auditService;
        _bankAccountService = bankAccountService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var workItems = await _bookingService.GetAdminBookingsAsync(null, cancellationToken);
        var operational = workItems
            .Where(item => item.Status is BookingStatus.Paid or BookingStatus.ReadyForPickup or BookingStatus.Rented or BookingStatus.PendingInspection or BookingStatus.AwaitingRefund)
            .OrderBy(item => item.Status == BookingStatus.PendingInspection ? 0 : 1)
            .ThenBy(item => item.PickupDate)
            .Take(12)
            .ToList();

        var model = new StaffDashboardViewModel
        {
            TodayPickups = await _dbContext.Bookings.AsNoTracking().CountAsync(item => item.PickupDate >= today && item.PickupDate < tomorrow && item.Status == BookingStatus.ReadyForPickup, cancellationToken),
            TodayReturns = await _dbContext.Bookings.AsNoTracking().CountAsync(item => item.ReturnDate >= today && item.ReturnDate < tomorrow && item.Status == BookingStatus.Rented, cancellationToken),
            PendingInspections = await _dbContext.Bookings.AsNoTracking().CountAsync(item => item.Status == BookingStatus.PendingInspection, cancellationToken),
            ApprovedRefundBookings = await _dbContext.Payments.AsNoTracking().Where(item => item.Type == PaymentType.Refund && item.Status == PaymentStatus.RefundApproved).Select(item => item.BookingId).Distinct().CountAsync(cancellationToken),
            PaidWaitingPreparation = await _dbContext.Bookings.AsNoTracking().CountAsync(item => item.Status == BookingStatus.Paid, cancellationToken),
            WorkItems = operational
        };
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Bookings(BookingStatus? status, string? query, CancellationToken cancellationToken)
    {
        if (status.HasValue && !OperationalStatuses.Contains(status.Value)) status = null;
        ViewBag.Status = status;
        ViewBag.Query = query;
        var bookings = await _bookingService.GetAdminBookingsAsync(status, cancellationToken);
        var filtered = bookings.Where(item => OperationalStatuses.Contains(item.Status)).ToList();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var keyword = query.Trim();
            var hasId = int.TryParse(keyword.TrimStart('#'), out var bookingId);
            filtered = filtered.Where(item =>
                (hasId && item.BookingId == bookingId) ||
                item.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.CustomerPhone) && item.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                item.VehicleName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.LicensePlate.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return View(filtered);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var booking = await _bookingService.GetAdminBookingAsync(id, cancellationToken);
        if (booking is null) return NotFound();
        if (!OperationalStatuses.Contains(booking.Status))
        {
            TempData["ErrorMessage"] = "Đơn này chưa tới bước nghiệp vụ của nhân viên.";
            return RedirectToAction(nameof(Bookings));
        }

        var records = await _dbContext.Bookings.AsNoTracking()
            .Include(item => item.Handover).Include(item => item.VehicleReturn).Include(item => item.Payments)
            .FirstAsync(item => item.BookingId == id, cancellationToken);

        var staffIds = new[]
        {
            records.Handover?.IdentityVerifiedByStaffId, records.Handover?.SignedDocumentVerifiedByStaffId,
            records.VehicleReturn?.IdentityVerifiedByStaffId, records.VehicleReturn?.SignedDocumentVerifiedByStaffId
        }.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().Cast<string>().ToArray();

        var staffNames = staffIds.Length == 0 ? new Dictionary<string, string>() :
            await _dbContext.Users.AsNoTracking().Where(user => staffIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
        var refunds = records.Payments.Where(item => item.Type == PaymentType.Refund).ToList();

        return View(new StaffBookingDetailsViewModel
        {
            Booking = booking,
            HandoverIdentityVerified = records.Handover?.CustomerIdentityVerified == true,
            HandoverIdentityVerifiedBy = ResolveStaffName(staffNames, records.Handover?.IdentityVerifiedByStaffId),
            HandoverIdentityVerifiedAt = records.Handover?.IdentityVerifiedAt,
            HandoverSignedDocumentVerified = records.Handover?.SignedDocumentVerified == true,
            HandoverSignedVerifiedBy = ResolveStaffName(staffNames, records.Handover?.SignedDocumentVerifiedByStaffId),
            HandoverSignedVerifiedAt = records.Handover?.SignedDocumentVerifiedAt,
            HandoverSignedPaths = FindSignedPaths(records.Handover?.ImagePaths, HandoverSignedMarker),
            ReturnIdentityVerified = records.VehicleReturn?.CustomerIdentityVerified == true,
            ReturnIdentityVerifiedBy = ResolveStaffName(staffNames, records.VehicleReturn?.IdentityVerifiedByStaffId),
            ReturnIdentityVerifiedAt = records.VehicleReturn?.IdentityVerifiedAt,
            ReturnSignedDocumentVerified = records.VehicleReturn?.SignedDocumentVerified == true,
            ReturnSignedVerifiedBy = ResolveStaffName(staffNames, records.VehicleReturn?.SignedDocumentVerifiedByStaffId),
            ReturnSignedVerifiedAt = records.VehicleReturn?.SignedDocumentVerifiedAt,
            ReturnSignedPaths = FindSignedPaths(records.VehicleReturn?.ImagePaths, ReturnSignedMarker),
            HasApprovedRefund = refunds.Any(item => item.Status == PaymentStatus.RefundApproved),
            HasUnapprovedRefund = refunds.Any(item => item.Status == PaymentStatus.AwaitingRefund),
            RefundAmount = refunds.Where(item => item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved).Sum(item => item.Amount)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(int bookingId, CancellationToken cancellationToken)
    {
        var result = await _bookingService.MarkReadyForPickupAsync(bookingId, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded ? "Đã xác nhận xe sẵn sàng để bàn giao." : string.Join("; ", result.Errors);
        if (result.Succeeded)
            await WriteAuditAsync("StaffMarkReady", nameof(Booking), bookingId, $"Nhân viên xác nhận xe sẵn sàng giao cho đơn #{bookingId}.", cancellationToken);
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpGet]
    public async Task<IActionResult> CounterRental(CancellationToken cancellationToken)
    {
        var model = new StaffCounterRentalViewModel();
        await PopulateCounterOptionsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CounterRental(StaffCounterRentalViewModel model, CancellationToken cancellationToken)
    {
        model.PaymentMethod = PaymentMethods.Cash;
        if (!ModelState.IsValid)
        {
            await PopulateCounterOptionsAsync(model, cancellationToken);
            return View(model);
        }

        if (!await IsActiveCustomerAsync(model.CustomerId, cancellationToken))
        {
            ModelState.AddModelError(
                nameof(model.CustomerId),
                "Khách hàng không hợp lệ, đã bị khóa hoặc không thuộc vai trò Customer.");
            await PopulateCounterOptionsAsync(model, cancellationToken);
            return View(model);
        }

        var createResult = await _bookingService.CreateAsync(
            model.CustomerId,
            new CreateBookingRequest(
                model.VehicleId,
                model.PickupDate,
                model.ReturnDate,
                VehiclePickupMethod.StorePickup,
                null,
                null,
                null),
            cancellationToken);

        if (!createResult.Succeeded || !createResult.BookingId.HasValue)
        {
            foreach (var error in createResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }
            await PopulateCounterOptionsAsync(model, cancellationToken);
            return View(model);
        }

        var bookingId = createResult.BookingId.Value;
        var confirmResult = await _bookingService.ConfirmAsync(bookingId, cancellationToken);
        if (!confirmResult.Succeeded)
        {
            var failedBooking = await _dbContext.Bookings.FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);
            if (failedBooking is not null)
            {
                failedBooking.Status = BookingStatus.Rejected;
                failedBooking.CancelReason = "Tạo đơn tại quầy không hoàn tất: " + string.Join("; ", confirmResult.Errors);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            ModelState.AddModelError(string.Empty, string.Join("; ", confirmResult.Errors));
            await PopulateCounterOptionsAsync(model, cancellationToken);
            return View(model);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var booking = await _dbContext.Bookings.Include(item => item.Payments).FirstAsync(item => item.BookingId == bookingId, cancellationToken);
        var paidAt = DateTime.UtcNow;
        foreach (var payment in booking.Payments.Where(item => (item.Type is PaymentType.Rental or PaymentType.Deposit) && item.Status == PaymentStatus.Pending))
        {
            payment.Status = PaymentStatus.Paid;
            payment.Method = PaymentMethods.Cash;
            payment.PaidAt = paidAt;
            payment.TransactionCode = $"CASH-{bookingId}-{paidAt:yyyyMMddHHmmss}";
        }
        var rentalPaid = booking.Payments.Any(item => item.Type == PaymentType.Rental && item.Status == PaymentStatus.Paid);
        var depositPaid = booking.DepositAmount <= 0 || booking.Payments.Where(item => item.Type == PaymentType.Deposit && item.Status == PaymentStatus.Paid).Sum(item => item.Amount) >= booking.DepositAmount;
        if (!rentalPaid || !depositPaid)
        {
            await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(string.Empty, "Không thể ghi nhận đủ tiền thuê và tiền cọc bằng tiền mặt.");
            await PopulateCounterOptionsAsync(model, cancellationToken);
            return View(model);
        }
        booking.Status = BookingStatus.Paid;
        _dbContext.Notifications.Add(new Notification { UserId = booking.CustomerId, Title = "Đã thanh toán tại quầy", Message = $"Đơn #{booking.BookingId}: SmartCar đã ghi nhận đủ tiền thuê và cọc bằng tiền mặt tại quầy." });
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await WriteAuditAsync("StaffCounterRentalCash", nameof(Booking), bookingId, $"Nhân viên tạo đơn thuê tại quầy #{bookingId} và thu tiền mặt.", cancellationToken);
        TempData["SuccessMessage"] = $"Đã tạo đơn #{bookingId} tại quầy và ghi nhận thanh toán tiền mặt. Tiếp tục chuẩn bị xe.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyHandoverSigned(int bookingId, bool signedCopyConfirmed, CancellationToken cancellationToken)
    {
        if (!signedCopyConfirmed) { TempData["ErrorMessage"] = "Phải mở bản ký và xác nhận đã đối chiếu chữ ký trước khi bắt đầu chuyến."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var booking = await _dbContext.Bookings.Include(item => item.Handover).Include(item => item.Vehicle).FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);
        if (booking?.Handover is null) { TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        if (!booking.Handover.CustomerIdentityVerified) { TempData["ErrorMessage"] = "Chưa xác minh đúng CCCD của người nhận xe."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        if (FindSignedPaths(booking.Handover.ImagePaths, HandoverSignedMarker).Count == 0) { TempData["ErrorMessage"] = "Chưa có ảnh/scan biên bản giao xe đã ký."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        if (booking.Status != BookingStatus.ReadyForPickup) { TempData["ErrorMessage"] = "Đơn không còn ở trạng thái sẵn sàng giao xe."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        if (booking.Vehicle.Status != VehicleStatus.Available) { TempData["ErrorMessage"] = "Xe không còn ở trạng thái sẵn sàng để giao."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        var staffId = CurrentUserId();
        booking.Handover.SignedDocumentVerified = true; booking.Handover.SignedDocumentVerifiedByStaffId = staffId; booking.Handover.SignedDocumentVerifiedAt = DateTime.UtcNow;
        booking.Status = BookingStatus.Rented; booking.Vehicle.Status = VehicleStatus.Rented; booking.Vehicle.CurrentMileage = booking.Handover.Mileage;
        _dbContext.Notifications.Add(new Notification { UserId = booking.CustomerId, Title = "Đã bàn giao xe", Message = $"Đơn #{booking.BookingId} đã được nhân viên đối chiếu người nhận, xác minh bản ký và bắt đầu chuyến thuê." });
        await _dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        await WriteAuditAsync("StaffVerifySignedHandover", nameof(VehicleHandover), bookingId, $"Nhân viên xác minh bản ký giao xe và bắt đầu chuyến #{bookingId}.", cancellationToken);
        TempData["SuccessMessage"] = "Đã xác minh bản ký giao xe. Chuyến thuê đã bắt đầu.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyReturnSigned(int bookingId, bool signedCopyConfirmed, CancellationToken cancellationToken)
    {
        if (!signedCopyConfirmed) { TempData["ErrorMessage"] = "Phải mở bản ký và xác nhận đã đối chiếu chữ ký trước khi quyết toán."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        var booking = await _dbContext.Bookings.Include(item => item.VehicleReturn).FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);
        if (booking?.VehicleReturn is null) { TempData["ErrorMessage"] = "Không tìm thấy biên bản trả xe."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        if (!booking.VehicleReturn.CustomerIdentityVerified) { TempData["ErrorMessage"] = "Chưa xác minh đúng CCCD của người trả xe."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        if (FindSignedPaths(booking.VehicleReturn.ImagePaths, ReturnSignedMarker).Count == 0) { TempData["ErrorMessage"] = "Chưa có ảnh/scan biên bản trả xe đã ký."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        if (booking.Status != BookingStatus.PendingInspection) { TempData["ErrorMessage"] = "Đơn không còn ở bước kiểm tra trả xe."; return RedirectToAction(nameof(Details), new { id = bookingId }); }
        var staffId = CurrentUserId();
        booking.VehicleReturn.SignedDocumentVerified = true; booking.VehicleReturn.SignedDocumentVerifiedByStaffId = staffId; booking.VehicleReturn.SignedDocumentVerifiedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync("StaffVerifySignedReturn", nameof(VehicleReturn), bookingId, $"Nhân viên xác minh bản ký trả xe của đơn #{bookingId}.", cancellationToken);
        TempData["SuccessMessage"] = "Đã xác minh bản ký trả xe. Có thể tiếp tục đối chiếu và quyết toán.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpGet]
    public async Task<IActionResult> Refunds(CancellationToken cancellationToken)
    {
        var approved = await _dbContext.Payments.AsNoTracking().Where(item => item.Type == PaymentType.Refund && item.Status == PaymentStatus.RefundApproved)
            .Include(item => item.Booking).ThenInclude(booking => booking.Vehicle).OrderBy(item => item.BookingId).ThenBy(item => item.PaymentId).ToListAsync(cancellationToken);
        var customerIds = approved.Select(item => item.Booking.CustomerId).Distinct().ToArray();
        var customerNames = customerIds.Length == 0 ? new Dictionary<string, string>() : await _dbContext.Users.AsNoTracking().Where(user => customerIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
        var model = new List<StaffRefundViewModel>();
        foreach (var group in approved.GroupBy(item => item.BookingId).OrderByDescending(group => group.Key))
        {
            var first = group.First();
            var bank = await _bankAccountService.GetDefaultAsync(first.Booking.CustomerId, cancellationToken);
            model.Add(new StaffRefundViewModel
            {
                BookingId = first.BookingId,
                CustomerId = first.Booking.CustomerId,
                CustomerName = customerNames.GetValueOrDefault(first.Booking.CustomerId, "Khách hàng"),
                VehicleName = first.Booking.Vehicle.VehicleName,
                LicensePlate = first.Booking.Vehicle.LicensePlate,
                TotalAmount = group.Sum(item => item.Amount),
                BankName = bank?.BankName,
                AccountNumber = bank?.AccountNumber,
                AccountHolderName = bank?.AccountHolderName,
                Status = PaymentStatus.RefundApproved,
                Lines = group.Select(item => new StaffRefundLineViewModel(item.PaymentId, item.Amount, item.Method, item.Status)).ToList()
            });
        }
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteRefund(
        int bookingId,
        string? transactionCode,
        CancellationToken cancellationToken)
    {
        transactionCode = transactionCode?.Trim();

        if (string.IsNullOrWhiteSpace(transactionCode))
        {
            TempData["ErrorMessage"] = "Vui lòng nhập mã giao dịch hoàn tiền.";
            return RedirectToAction(nameof(Refunds));
        }

        if (transactionCode.Length > 100)
        {
            TempData["ErrorMessage"] = "Mã giao dịch tối đa 100 ký tự.";
            return RedirectToAction(nameof(Refunds));
        }

        var customerId = await _dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.BookingId == bookingId)
            .Select(item => item.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê.";
            return RedirectToAction(nameof(Refunds));
        }

        var bank = await _bankAccountService.GetDefaultAsync(customerId, cancellationToken);
        if (bank is null)
        {
            TempData["ErrorMessage"] = "Khách chưa có tài khoản ngân hàng mặc định để nhận hoàn tiền.";
            return RedirectToAction(nameof(Refunds));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var duplicateCode = await _dbContext.Payments
            .AsNoTracking()
            .AnyAsync(
                item =>
                    item.Type == PaymentType.Refund &&
                    item.Status == PaymentStatus.Refunded &&
                    item.TransactionCode == transactionCode,
                cancellationToken);

        if (duplicateCode)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Mã giao dịch này đã được sử dụng cho một khoản hoàn tiền khác.";
            return RedirectToAction(nameof(Refunds));
        }

        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê.";
            return RedirectToAction(nameof(Refunds));
        }

        var hasWaitingApproval = booking.Payments.Any(item =>
            item.Type == PaymentType.Refund &&
            item.Status == PaymentStatus.AwaitingRefund);

        if (hasWaitingApproval)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Vẫn còn khoản hoàn chưa được chủ/Admin duyệt. Nhân viên chưa được phép chuyển tiền.";
            return RedirectToAction(nameof(Refunds));
        }

        var approvedRefunds = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Status == PaymentStatus.RefundApproved)
            .OrderBy(item => item.PaymentId)
            .ToList();

        if (approvedRefunds.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Đơn không có khoản hoàn nào đã được chủ/Admin duyệt.";
            return RedirectToAction(nameof(Refunds));
        }

        if (approvedRefunds.Any(item => item.Amount <= 0))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Có khoản hoàn tiền không hợp lệ.";
            return RedirectToAction(nameof(Refunds));
        }

        var refundedAt = DateTime.UtcNow;
        var total = approvedRefunds.Sum(item => item.Amount);

        foreach (var refund in approvedRefunds)
        {
            refund.Status = PaymentStatus.Refunded;
            refund.PaidAt = refundedAt;
            refund.TransactionCode = transactionCode;
        }

        var hasOpenRefund = booking.Payments.Any(item =>
            item.Type == PaymentType.Refund &&
            (item.Status == PaymentStatus.AwaitingRefund || item.Status == PaymentStatus.RefundApproved));

        if (booking.Status == BookingStatus.AwaitingRefund && !hasOpenRefund)
        {
            booking.Status = BookingStatus.Completed;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Hoàn tiền thành công",
            Message =
                $"Đơn #{booking.BookingId}: SmartCar đã hoàn tổng {total:N0} đồng vào " +
                $"{bank.BankName} - {bank.MaskedAccountNumber}. Mã giao dịch: {transactionCode}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await WriteAuditAsync(
            "StaffExecuteApprovedRefund",
            nameof(Payment),
            bookingId,
            $"Thực hiện khoản hoàn đã được duyệt cho đơn #{bookingId}: {total:N0} đồng, mã GD {transactionCode}.",
            cancellationToken);

        TempData["SuccessMessage"] = $"Đã hoàn {total:N0} đ cho đơn #{bookingId}.";
        return RedirectToAction(nameof(Refunds));
    }

    private async Task<bool> IsActiveCustomerAsync(
        string customerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return false;
        }

        var customerRoleId = await _dbContext.Roles
            .AsNoTracking()
            .Where(role => role.Name == RoleNames.Customer)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(customerRoleId))
        {
            return false;
        }

        return await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(user =>
                user.Id == customerId &&
                user.IsActive &&
                _dbContext.UserRoles.Any(userRole =>
                    userRole.UserId == user.Id &&
                    userRole.RoleId == customerRoleId),
                cancellationToken);
    }

    private async Task PopulateCounterOptionsAsync(StaffCounterRentalViewModel model, CancellationToken cancellationToken)
    {
        var customerRoleId = await _dbContext.Roles.AsNoTracking().Where(role => role.Name == RoleNames.Customer).Select(role => role.Id).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(customerRoleId)) model.Customers = Array.Empty<StaffOptionViewModel>();
        else
        {
            var customers = await _dbContext.Users.AsNoTracking().Where(user => user.IsActive && _dbContext.UserRoles.Any(userRole => userRole.UserId == user.Id && userRole.RoleId == customerRoleId))
                .OrderBy(user => user.FullName).Select(user => new { user.Id, user.FullName, user.Email, user.PhoneNumber }).ToListAsync(cancellationToken);
            model.Customers = customers.Select(user => new StaffOptionViewModel(user.Id, $"{user.FullName} · {user.Email} · {user.PhoneNumber ?? "-"}")).ToList();
        }
        var vehicles = await _dbContext.Vehicles
            .AsNoTracking()
            .Where(vehicle =>
                vehicle.Status == VehicleStatus.Available ||
                vehicle.Status == VehicleStatus.Rented)
            .OrderBy(vehicle => vehicle.VehicleName)
            .Select(vehicle => new
            {
                vehicle.VehicleId,
                vehicle.VehicleName,
                vehicle.LicensePlate,
                vehicle.DailyPrice,
                vehicle.Status
            })
            .ToListAsync(cancellationToken);
        model.Vehicles = vehicles.Select(vehicle => new StaffOptionViewModel(vehicle.VehicleId.ToString(), $"{vehicle.VehicleName} · {vehicle.LicensePlate} · {vehicle.DailyPrice:N0} đ/ngày · " + (vehicle.Status == VehicleStatus.Available ? "Có sẵn" : "Đang thuê - chỉ đặt lịch sau"))).ToList();
    }

    private static IReadOnlyList<string> FindSignedPaths(string? paths, string marker) => string.IsNullOrWhiteSpace(paths) ? Array.Empty<string>() : paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase)).ToArray();
    private static string? ResolveStaffName(IReadOnlyDictionary<string, string> staffNames, string? staffId) => !string.IsNullOrWhiteSpace(staffId) && staffNames.TryGetValue(staffId, out var name) ? name : null;
    private string CurrentUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    private Task WriteAuditAsync(string action, string entityName, int entityId, string description, CancellationToken cancellationToken) =>
        _auditService.WriteAsync(CurrentUserId(), action, entityName, entityId.ToString(), description, ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken: cancellationToken);
}
