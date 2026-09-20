using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Staff)]
public sealed class StaffController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

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
        var openRefundBookingIds = await GetOpenRefundBookingIdsAsync(cancellationToken);
        var workItems = await _bookingService.GetAdminBookingsAsync(null, cancellationToken);
        var operational = workItems
            .Where(item => BookingWorkflowRules.IsStaffWorkItem(
                item.Status,
                openRefundBookingIds.Contains(item.BookingId)))
            .OrderBy(item => item.Status == BookingStatus.PendingInspection ? 0
                : openRefundBookingIds.Contains(item.BookingId) ? 1
                : item.Status == BookingStatus.PendingConfirmation ? 2
                : 3)
            .ThenBy(item => item.PickupDate)
            .Take(12)
            .ToList();

        var model = new StaffDashboardViewModel
        {
            TodayPickups = await _dbContext.Bookings.AsNoTracking().CountAsync(
                item => item.PickupDate >= today && item.PickupDate < tomorrow && item.Status == BookingStatus.ReadyForPickup,
                cancellationToken),
            TodayReturns = await _dbContext.Bookings.AsNoTracking().CountAsync(
                item => item.ReturnDate >= today && item.ReturnDate < tomorrow && item.Status == BookingStatus.Rented,
                cancellationToken),
            PendingInspections = await _dbContext.Bookings.AsNoTracking().CountAsync(
                item => item.Status == BookingStatus.PendingInspection,
                cancellationToken),
            ApprovedRefundBookings = await _dbContext.Payments.AsNoTracking()
                .Where(item => item.Type == PaymentType.Refund && item.Status == PaymentStatus.RefundApproved)
                .Select(item => item.BookingId)
                .Distinct()
                .CountAsync(cancellationToken),
            PaidWaitingPreparation = await _dbContext.Bookings.AsNoTracking().CountAsync(
                item => item.Status == BookingStatus.Paid,
                cancellationToken),
            WorkItems = operational
        };
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Bookings(
        BookingStatus? status,
        string? query,
        CancellationToken cancellationToken)
    {
        ViewBag.Status = status;
        ViewBag.Query = query;

        var openRefundBookingIds = await GetOpenRefundBookingIdsAsync(cancellationToken);
        var bookings = await _bookingService.GetAdminBookingsAsync(null, cancellationToken);
        var filtered = bookings
            .Where(item => BookingWorkflowRules.IsStaffWorkItem(
                item.Status,
                openRefundBookingIds.Contains(item.BookingId)))
            .ToList();

        if (status.HasValue)
        {
            filtered = filtered.Where(item => item.Status == status.Value).ToList();
        }

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
        if (booking is null)
            return NotFound();

        var records = await _dbContext.Bookings.AsNoTracking()
            .Include(item => item.Handover)
            .Include(item => item.VehicleReturn)
            .Include(item => item.Payments)
            .FirstAsync(item => item.BookingId == id, cancellationToken);

        var refunds = records.Payments
            .Where(item => item.Type == PaymentType.Refund)
            .ToList();
        var hasOpenRefund = refunds.Any(item =>
            item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved);

        var staffCanView = BookingWorkflowRules.IsStaffWorkItem(booking.Status, hasOpenRefund)
            || booking.Status == BookingStatus.Completed;
        if (!staffCanView)
        {
            TempData["ErrorMessage"] = "Đơn này không còn công việc vận hành dành cho nhân viên.";
            return RedirectToAction(nameof(Bookings));
        }

        var staffIds = new[]
        {
            records.Handover?.IdentityVerifiedByStaffId,
            records.Handover?.SignedDocumentVerifiedByStaffId,
            records.VehicleReturn?.IdentityVerifiedByStaffId,
            records.VehicleReturn?.SignedDocumentVerifiedByStaffId
        }.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().Cast<string>().ToArray();

        var staffNames = staffIds.Length == 0
            ? new Dictionary<string, string>()
            : await _dbContext.Users.AsNoTracking()
                .Where(user => staffIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        return View(new StaffBookingDetailsViewModel
        {
            Booking = booking,
            StaffReviewedAt = records.StaffReviewedAt,
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
            RefundAmount = refunds
                .Where(item => item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved)
                .Sum(item => item.Amount)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(int bookingId, CancellationToken cancellationToken)
    {
        var result = await _bookingService.MarkReadyForPickupAsync(bookingId, cancellationToken);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xác nhận xe sẵn sàng để bàn giao."
            : string.Join("; ", result.Errors);
        if (result.Succeeded)
        {
            await WriteAuditAsync(
                "StaffMarkReady",
                nameof(Booking),
                bookingId,
                $"Nhân viên xác nhận xe sẵn sàng giao cho đơn #{bookingId}.",
                cancellationToken);
        }
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpGet]
    public IActionResult CounterRental() =>
        View(new StaffCounterRentalViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CounterRental(
        StaffCounterRentalViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(model);

        if (!await IsActiveCustomerAsync(model.CustomerId, cancellationToken))
        {
            ModelState.AddModelError(
                nameof(model.CustomerId),
                "Khách hàng không hợp lệ, đã bị khóa hoặc không thuộc vai trò Khách hàng.");
            return View(model);
        }

        if (await _bankAccountService.GetDefaultAsync(model.CustomerId, cancellationToken) is null)
        {
            ModelState.AddModelError(
                string.Empty,
                "Khách hàng chưa có tài khoản ngân hàng mặc định để nhận hoàn cọc/hoàn tiền. Hãy bổ sung tài khoản ngân hàng cho khách trước khi lập đơn tại quầy.");
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
            return View(model);
        }

        var bookingId = createResult.BookingId.Value;
        _dbContext.Notifications.Add(new Notification
        {
            UserId = model.CustomerId,
            Title = "Đã lập yêu cầu thuê xe tại quầy",
            Message = $"Nhân viên đã lập đơn #{bookingId}. Đơn đang chờ kiểm tra và quản trị viên duyệt trước khi thanh toán."
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            "StaffCreateCounterRental",
            nameof(Booking),
            bookingId,
            $"Nhân viên lập đơn thuê tại quầy #{bookingId}; khách/xe được chọn qua tìm kiếm, lịch xe sẽ tiếp tục được kiểm tra ở bước Staff review và Admin approval.",
            cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã lập đơn #{bookingId}. Hãy kiểm tra điều kiện đơn rồi gửi Admin duyệt trước khi thu tiền.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReceiveCash(int bookingId, CancellationToken cancellationToken)
    {
        var current = await _bookingService.GetAdminBookingAsync(bookingId, cancellationToken);
        if (current is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê.";
            return RedirectToAction(nameof(Bookings));
        }

        var currentHasPendingSwapAdjustment = current.Payments.Any(payment =>
            payment.Type == PaymentType.VehicleSwapAdjustment &&
            payment.Status == PaymentStatus.Pending);
        var canReceiveCash =
            current.Status == BookingStatus.PendingPayment ||
            (current.Status == BookingStatus.Paid && currentHasPendingSwapAdjustment);

        if (!canReceiveCash)
        {
            TempData["ErrorMessage"] =
                "Chỉ được thu tiền mặt khi đơn đang chờ thanh toán hoặc có chênh lệch đổi xe chưa thu.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        var bookingHasPendingSwapAdjustment = booking?.Payments.Any(payment =>
            payment.Type == PaymentType.VehicleSwapAdjustment &&
            payment.Status == PaymentStatus.Pending) == true;
        var stillCanReceiveCash = booking is not null &&
            (booking.Status == BookingStatus.PendingPayment ||
             (booking.Status == BookingStatus.Paid && bookingHasPendingSwapAdjustment));

        if (!stillCanReceiveCash)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Trạng thái đơn hoặc khoản cần thu đã thay đổi. Vui lòng tải lại trước khi thu tiền.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var upfrontPayments = booking!.Payments
            .Where(item => item.Type is PaymentType.Rental or PaymentType.Deposit)
            .ToList();
        var settlementPayments = booking.Payments
            .Where(item =>
                item.Type is PaymentType.Rental or
                    PaymentType.Deposit or
                    PaymentType.VehicleSwapAdjustment)
            .ToList();

        if (settlementPayments.Any(item => item.Status == PaymentStatus.AwaitingConfirmation))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Đơn đang có chuyển khoản chờ Staff đối soát. Không được đồng thời ghi nhận tiền mặt.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var paidAt = DateTime.UtcNow;
        var transactionCode = $"CASH-{bookingId}-{paidAt:yyyyMMddHHmmss}";

        var requiredRentalAmount = Math.Max(
            0m,
            booking.TotalAmount - booking.AdditionalAmount);
        var grossRentalPaidBefore = booking.Payments
            .Where(item =>
                item.Status == PaymentStatus.Paid &&
                item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
            .Sum(item => item.Amount);
        var rentalRefundPlanned = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.VehicleSwapRefund &&
                item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveRentalPaidBefore = BookingWorkflowRules.CalculateEffectivePaid(
            grossRentalPaidBefore,
            rentalRefundPlanned);
        var outstandingRental = BookingWorkflowRules.CalculateOutstandingRental(
            requiredRentalAmount,
            effectiveRentalPaidBefore);

        var pendingRentals = upfrontPayments
            .Where(item =>
                item.Type == PaymentType.Rental &&
                item.Status == PaymentStatus.Pending)
            .OrderBy(item => item.PaymentId)
            .ToList();

        Payment? rentalCollectedNow = null;
        if (outstandingRental > 0m)
        {
            rentalCollectedNow = pendingRentals.FirstOrDefault();
            if (rentalCollectedNow is null)
            {
                rentalCollectedNow = new Payment
                {
                    BookingId = booking.BookingId,
                    Type = PaymentType.Rental
                };
                booking.Payments.Add(rentalCollectedNow);
            }

            rentalCollectedNow.Amount = outstandingRental;
            rentalCollectedNow.Status = PaymentStatus.Paid;
            rentalCollectedNow.Method = PaymentMethods.Cash;
            rentalCollectedNow.PaidAt = paidAt;
            rentalCollectedNow.TransactionCode = transactionCode;
        }

        foreach (var staleRental in pendingRentals.Where(item => item != rentalCollectedNow))
        {
            staleRental.Status = PaymentStatus.Failed;
            staleRental.Method = PaymentMethods.NotSelected;
            staleRental.PaidAt = null;
            staleRental.TransactionCode = null;
        }

        var grossDepositPaidBefore = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.Paid)
            .Sum(item => item.Amount);
        var depositRefundPlanned = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.DepositRefund &&
                item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveDepositPaidBefore = BookingWorkflowRules.CalculateEffectivePaid(
            grossDepositPaidBefore,
            depositRefundPlanned);
        var outstandingDeposit = BookingWorkflowRules.CalculateOutstandingDeposit(
            booking.DepositAmount,
            effectiveDepositPaidBefore);

        var pendingDeposits = upfrontPayments
            .Where(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.Pending)
            .OrderBy(item => item.PaymentId)
            .ToList();

        Payment? depositCollectedNow = null;
        if (outstandingDeposit > 0m)
        {
            depositCollectedNow = pendingDeposits.FirstOrDefault();
            if (depositCollectedNow is null)
            {
                depositCollectedNow = new Payment
                {
                    BookingId = booking.BookingId,
                    Type = PaymentType.Deposit
                };
                booking.Payments.Add(depositCollectedNow);
            }

            depositCollectedNow.Amount = outstandingDeposit;
            depositCollectedNow.Method = PaymentMethods.Cash;
            depositCollectedNow.Status = PaymentStatus.Paid;
            depositCollectedNow.PaidAt = paidAt;
            depositCollectedNow.TransactionCode = transactionCode;
        }

        foreach (var staleDeposit in pendingDeposits.Where(item => item != depositCollectedNow))
        {
            // Khoản Pending dư là dữ liệu cũ/trùng. Không được biến thành Paid vì sẽ thu thừa cọc.
            staleDeposit.Status = PaymentStatus.Failed;
            staleDeposit.Method = PaymentMethods.NotSelected;
            staleDeposit.PaidAt = null;
            staleDeposit.TransactionCode = null;
        }

        foreach (var staleSwapAdjustment in booking.Payments.Where(item =>
                     item.Type == PaymentType.VehicleSwapAdjustment &&
                     item.Status == PaymentStatus.Pending))
        {
            // Thu tiền mặt trực tiếp theo số còn thiếu cuối cùng; không để khoản chênh lệch cũ
            // tiếp tục tồn tại và có thể bị thu lần hai.
            staleSwapAdjustment.Status = PaymentStatus.Failed;
            staleSwapAdjustment.Method = PaymentMethods.NotSelected;
            staleSwapAdjustment.PaidAt = null;
            staleSwapAdjustment.TransactionCode = null;
        }

        var grossRentalPaid = booking.Payments
            .Where(item =>
                item.Status == PaymentStatus.Paid &&
                item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
            .Sum(item => item.Amount);
        var grossDepositPaid = booking.Payments
            .Where(item => item.Type == PaymentType.Deposit && item.Status == PaymentStatus.Paid)
            .Sum(item => item.Amount);
        var effectiveRentalPaid = BookingWorkflowRules.CalculateEffectivePaid(
            grossRentalPaid,
            rentalRefundPlanned);
        var effectiveDepositPaid = BookingWorkflowRules.CalculateEffectivePaid(
            grossDepositPaid,
            depositRefundPlanned);

        if (!BookingWorkflowRules.HasRequiredUpfrontPayment(
                requiredRentalAmount,
                effectiveRentalPaid,
                booking.DepositAmount,
                effectiveDepositPaid))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Đơn chưa có đủ khoản tiền thuê hoặc tiền cọc cần thu nên không thể ghi Paid.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        booking.Status = BookingStatus.Paid;
        booking.ReservationExpiresAt = null;
        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã thanh toán tại quầy",
            Message = $"Đơn #{booking.BookingId}: SmartCar đã ghi nhận đủ tiền thuê và tiền cọc bằng tiền mặt tại quầy."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await WriteAuditAsync(
            "StaffReceiveCounterCash",
            nameof(Booking),
            bookingId,
            $"Nhân viên thu tiền mặt cho đơn #{bookingId} sau khi Admin duyệt. Mã giao dịch nội bộ: {transactionCode}.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã ghi nhận đủ tiền mặt. Đơn chuyển sang Đã thanh toán.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyHandoverSigned(
        int bookingId,
        bool signedCopyConfirmed,
        CancellationToken cancellationToken)
    {
        if (!signedCopyConfirmed)
        {
            TempData["ErrorMessage"] = "Phải mở bản ký và xác nhận đã đối chiếu chữ ký trước khi bắt đầu chuyến.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var booking = await _dbContext.Bookings
            .Include(item => item.Handover)
            .Include(item => item.Vehicle)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking?.Handover is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy biên bản giao xe.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }
        if (!booking.Handover.CustomerIdentityVerified)
        {
            TempData["ErrorMessage"] = "Chưa xác minh đúng danh tính người nhận xe.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }
        if (FindSignedPaths(booking.Handover.ImagePaths, HandoverSignedMarker).Count == 0)
        {
            TempData["ErrorMessage"] = "Chưa có ảnh/scan biên bản giao xe đã ký.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }
        if (booking.Status != BookingStatus.ReadyForPickup)
        {
            TempData["ErrorMessage"] = "Đơn không còn ở trạng thái sẵn sàng giao xe.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }
        if (booking.Vehicle.Status != VehicleStatus.Available)
        {
            TempData["ErrorMessage"] = "Xe không còn ở trạng thái sẵn sàng để giao.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }
        var grossHandoverRentalPaid = booking.Payments
            .Where(item =>
                item.Status == PaymentStatus.Paid &&
                item.Type is PaymentType.Rental or PaymentType.VehicleSwapAdjustment)
            .Sum(item => item.Amount);
        var handoverRentalRefundPlanned = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.VehicleSwapRefund &&
                item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveHandoverRentalPaid = BookingWorkflowRules.CalculateEffectivePaid(
            grossHandoverRentalPaid,
            handoverRentalRefundPlanned);

        var grossHandoverDepositPaid = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Deposit &&
                item.Status == PaymentStatus.Paid)
            .Sum(item => item.Amount);
        var handoverDepositRefundPlanned = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Method == PaymentMethods.DepositRefund &&
                item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
            .Sum(item => item.Amount);
        var effectiveHandoverDepositPaid = BookingWorkflowRules.CalculateEffectivePaid(
            grossHandoverDepositPaid,
            handoverDepositRefundPlanned);

        var requiredHandoverRentalAmount = Math.Max(
            0m,
            booking.TotalAmount - booking.AdditionalAmount);

        if (!BookingWorkflowRules.HasRequiredUpfrontPayment(
                requiredHandoverRentalAmount,
                effectiveHandoverRentalPaid,
                booking.DepositAmount,
                effectiveHandoverDepositPaid))
        {
            TempData["ErrorMessage"] =
                "Không thể bắt đầu chuyến vì hệ thống không còn ghi nhận đủ tiền thuê và tiền cọc.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        if (DateTime.Now < booking.PickupDate)
        {
            TempData["ErrorMessage"] =
                $"Chưa đến giờ nhận xe đã đặt ({booking.PickupDate:dd/MM/yyyy HH:mm}). Không thể bắt đầu chuyến sớm hơn lịch.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var staffId = CurrentUserId();
        var actualHandoverAt = DateTime.Now;
        booking.Handover.HandoverAt = actualHandoverAt;
        booking.Handover.SignedDocumentVerified = true;
        booking.Handover.SignedDocumentVerifiedByStaffId = staffId;
        booking.Handover.SignedDocumentVerifiedAt = DateTime.UtcNow;
        booking.Status = BookingStatus.Rented;
        booking.Vehicle.Status = VehicleStatus.Rented;
        booking.Vehicle.CurrentMileage = booking.Handover.Mileage;

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Đã bàn giao xe",
            Message = $"Đơn #{booking.BookingId} bắt đầu chuyến lúc {actualHandoverAt:dd/MM/yyyy HH:mm} sau khi nhân viên xác minh đúng người và bản ký."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await WriteAuditAsync(
            "StaffVerifySignedHandover",
            nameof(VehicleHandover),
            bookingId,
            $"Nhân viên xác minh bản ký và bắt đầu chuyến #{bookingId} lúc {actualHandoverAt:dd/MM/yyyy HH:mm}.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã xác minh bản ký. Thời điểm bàn giao thực tế đã được chốt và chuyến thuê bắt đầu.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyReturnSigned(
        int bookingId,
        bool signedCopyConfirmed,
        CancellationToken cancellationToken)
    {
        if (!signedCopyConfirmed)
        {
            TempData["ErrorMessage"] = "Phải mở bản ký và xác nhận đã đối chiếu chữ ký trước khi quyết toán.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.VehicleReturn)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);
        if (booking?.VehicleReturn is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Không tìm thấy biên bản trả xe.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        if (booking.VehicleReturn.SignedDocumentVerified)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["SuccessMessage"] =
                "Bản ký trả xe đã được nhân viên khác xác minh trước đó. Hệ thống giữ nguyên người xác minh đầu tiên.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        if (!booking.VehicleReturn.CustomerIdentityVerified)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Chưa xác minh đúng CCCD của người trả xe.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }
        if (FindSignedPaths(booking.VehicleReturn.ImagePaths, ReturnSignedMarker).Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Chưa có ảnh/scan biên bản trả xe đã ký.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }
        if (booking.Status != BookingStatus.PendingInspection)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Đơn không còn ở bước kiểm tra trả xe.";
            return RedirectToAction(nameof(Details), new { id = bookingId });
        }

        var staffId = CurrentUserId();
        booking.VehicleReturn.SignedDocumentVerified = true;
        booking.VehicleReturn.SignedDocumentVerifiedByStaffId = staffId;
        booking.VehicleReturn.SignedDocumentVerifiedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await WriteAuditAsync(
            "StaffVerifySignedReturn",
            nameof(VehicleReturn),
            bookingId,
            $"Nhân viên xác minh bản ký trả xe của đơn #{bookingId}.",
            cancellationToken);

        TempData["SuccessMessage"] = "Đã xác minh bản ký trả xe. Có thể tiếp tục đối chiếu và quyết toán.";
        return RedirectToAction(nameof(Details), new { id = bookingId });
    }

    [HttpGet]
    public async Task<IActionResult> Refunds(CancellationToken cancellationToken)
    {
        var openRefunds = await _dbContext.Payments.AsNoTracking()
            .Where(item =>
                item.Type == PaymentType.Refund &&
                (item.Status == PaymentStatus.AwaitingRefund ||
                 item.Status == PaymentStatus.RefundApproved))
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Vehicle)
            .OrderBy(item => item.BookingId)
            .ThenBy(item => item.PaymentId)
            .ToListAsync(cancellationToken);

        // Trang này chỉ hiển thị booking đã có ít nhất một khoản được Admin duyệt.
        // Tuy nhiên phải giữ cả AwaitingRefund trong cùng batch để Staff không nhìn thấy
        // một nút "được phép chuyển" trong khi backend chắc chắn sẽ chặn chuyển tách lẻ.
        var actionableGroups = openRefunds
            .GroupBy(item => item.BookingId)
            .Where(group => group.Any(item => item.Status == PaymentStatus.RefundApproved))
            .OrderByDescending(group => group.Key)
            .ToList();

        var bookingIds = actionableGroups
            .Select(group => group.Key)
            .ToArray();

        var outstandingTrafficFineByBooking = bookingIds.Length == 0
            ? new Dictionary<int, decimal>()
            : await _dbContext.Payments.AsNoTracking()
                .Where(payment =>
                    bookingIds.Contains(payment.BookingId) &&
                    payment.Type == PaymentType.TrafficFine &&
                    (payment.Status == PaymentStatus.Pending ||
                     payment.Status == PaymentStatus.AwaitingConfirmation ||
                     payment.Status == PaymentStatus.Failed))
                .GroupBy(payment => payment.BookingId)
                .Select(group => new
                {
                    BookingId = group.Key,
                    Amount = group.Sum(payment => payment.Amount)
                })
                .Where(item => item.Amount > 0m)
                .ToDictionaryAsync(
                    item => item.BookingId,
                    item => item.Amount,
                    cancellationToken);

        var openOverdueDebtRows = bookingIds.Length == 0
            ? new List<(decimal Amount, string? TransactionCode)>()
            : (await _dbContext.Payments.AsNoTracking()
                .Where(payment =>
                    payment.Amount > 0m &&
                    (payment.Status == PaymentStatus.Pending ||
                     payment.Status == PaymentStatus.AwaitingConfirmation) &&
                    payment.TransactionCode != null &&
                    (
                        payment.Type == PaymentType.OverdueCompensationDebt ||
                        (payment.Type == PaymentType.AdditionalCharge &&
                         payment.TransactionCode.StartsWith(OverdueCompensationLedger.DebtPrefix))
                    ))
                .Select(payment => new
                {
                    payment.Amount,
                    payment.TransactionCode
                })
                .ToListAsync(cancellationToken))
                .Select(payment => (payment.Amount, payment.TransactionCode))
                .ToList();

        var unfundedCompensationByAffectedBooking = openOverdueDebtRows
            .Select(payment => new
            {
                payment.Amount,
                Parsed = OverdueCompensationLedger.TryParseDebtRelation(
                    payment.TransactionCode,
                    out _,
                    out var affectedBookingId),
                AffectedBookingId = affectedBookingId
            })
            .Where(item =>
                item.Parsed &&
                bookingIds.Contains(item.AffectedBookingId))
            .GroupBy(item => item.AffectedBookingId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.Amount));

        var customerIds = actionableGroups
            .Select(group => group.First().Booking.CustomerId)
            .Distinct()
            .ToArray();
        var customerNames = customerIds.Length == 0
            ? new Dictionary<string, string>()
            : await _dbContext.Users.AsNoTracking()
                .Where(user => customerIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        var model = new List<StaffRefundViewModel>();
        foreach (var group in actionableGroups)
        {
            var first = group.First();
            var approvedLines = group
                .Where(item => item.Status == PaymentStatus.RefundApproved)
                .ToList();
            var awaitingApprovalAmount = group
                .Where(item => item.Status == PaymentStatus.AwaitingRefund)
                .Sum(item => item.Amount);
            var bank = await _bankAccountService.GetDefaultAsync(
                first.Booking.CustomerId,
                cancellationToken);

            model.Add(new StaffRefundViewModel
            {
                BookingId = first.BookingId,
                CustomerId = first.Booking.CustomerId,
                CustomerName = customerNames.GetValueOrDefault(first.Booking.CustomerId, "Khách hàng"),
                VehicleName = first.Booking.Vehicle.VehicleName,
                LicensePlate = first.Booking.Vehicle.LicensePlate,
                TotalAmount = approvedLines.Sum(item => item.Amount),
                AwaitingApprovalAmount = awaitingApprovalAmount,
                OutstandingTrafficFineAmount =
                    outstandingTrafficFineByBooking.GetValueOrDefault(first.BookingId),
                UnfundedCompensationAmount =
                    unfundedCompensationByAffectedBooking.GetValueOrDefault(first.BookingId),
                BankName = bank?.BankName,
                AccountNumber = bank?.AccountNumber,
                AccountHolderName = bank?.AccountHolderName,
                Status = PaymentStatus.RefundApproved,
                Lines = group
                    .Select(item => new StaffRefundLineViewModel(
                        item.PaymentId,
                        item.Amount,
                        item.Method,
                        item.Status,
                        item.TransactionCode,
                        item.LedgerReference))
                    .ToList()
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

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        // Khóa booking + refund state trước, sau đó mới đọc tài khoản nhận tiền trong
        // CÙNG transaction. UserBankAccountService sẽ enlist vào transaction này,
        // nên không thể đổi tài khoản ở giữa lúc Staff đang hoàn tiền.
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
            item.Type == PaymentType.Refund && item.Status == PaymentStatus.AwaitingRefund);
        if (hasWaitingApproval)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Vẫn còn khoản hoàn chưa được Admin duyệt. Staff chưa được phép chuyển tiền.";
            return RedirectToAction(nameof(Refunds));
        }

        var approvedRefunds = booking.Payments
            .Where(item => item.Type == PaymentType.Refund && item.Status == PaymentStatus.RefundApproved)
            .OrderBy(item => item.PaymentId)
            .ToList();
        if (approvedRefunds.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Đơn không có khoản hoàn nào đã được Admin duyệt.";
            return RedirectToAction(nameof(Refunds));
        }
        if (approvedRefunds.Any(item => item.Amount <= 0))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Có khoản hoàn tiền không hợp lệ.";
            return RedirectToAction(nameof(Refunds));
        }

        if (approvedRefunds.Any(item =>
                item.Method == PaymentMethods.CompensationRefund &&
                !CompensationLedger.IsFundedRefund(
                    item.LedgerReference,
                    item.TransactionCode)))
        {
            var openOverdueDebts = await _dbContext.Payments
                .AsNoTracking()
                .Where(payment =>
                    payment.Amount > 0m &&
                    (payment.Status == PaymentStatus.Pending ||
                     payment.Status == PaymentStatus.AwaitingConfirmation) &&
                    payment.TransactionCode != null &&
                    (
                        payment.Type == PaymentType.OverdueCompensationDebt ||
                        (payment.Type == PaymentType.AdditionalCharge &&
                         payment.TransactionCode.StartsWith(OverdueCompensationLedger.DebtPrefix))
                    ))
                .Select(payment => new
                {
                    payment.Amount,
                    payment.TransactionCode
                })
                .ToListAsync(cancellationToken);

            var unfundedCompensation = openOverdueDebts
                .Where(payment =>
                    OverdueCompensationLedger.TryParseDebtRelation(
                        payment.TransactionCode,
                        out _,
                        out var affectedBookingId) &&
                    affectedBookingId == booking.BookingId)
                .Sum(payment => payment.Amount);

            if (unfundedCompensation > 0m)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] =
                    $"Khoản bồi thường của đơn #{booking.BookingId} còn {unfundedCompensation:N0} đồng chưa được thực thu từ khách gây ảnh hưởng. " +
                    "Không được chuyển tiền cho đến khi phần này được thanh toán/đối soát xong.";
                return RedirectToAction(nameof(Refunds));
            }
        }

        if (approvedRefunds.Any(item => item.Method == PaymentMethods.DepositRefund))
        {
            var outstandingTrafficFine = booking.Payments
                .Where(payment => BookingWorkflowRules.IsOutstandingTrafficFine(
                    payment.Type,
                    payment.Status,
                    payment.Amount))
                .Sum(payment => payment.Amount);

            if (outstandingTrafficFine > 0m)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] =
                    $"Đơn vừa phát sinh {outstandingTrafficFine:N0} đồng phạt/vi phạm chưa xử lý. " +
                    "Chưa được chuyển khoản hoàn cọc cho đến khi nghĩa vụ này được xử lý.";
                return RedirectToAction(nameof(Refunds));
            }
        }

        var duplicateCode = await _dbContext.Payments.AsNoTracking().AnyAsync(
            item => item.Type == PaymentType.Refund &&
                    item.Status == PaymentStatus.Refunded &&
                    item.TransactionCode == transactionCode,
            cancellationToken);
        if (duplicateCode)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "Mã giao dịch này đã được sử dụng cho một khoản hoàn tiền khác.";
            return RedirectToAction(nameof(Refunds));
        }

        var bank = await _bankAccountService.GetDefaultAsync(
            booking.CustomerId,
            cancellationToken);
        if (bank is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] =
                "Khách chưa có tài khoản ngân hàng mặc định để nhận hoàn tiền.";
            return RedirectToAction(nameof(Refunds));
        }

        var refundedAt = DateTime.UtcNow;
        var total = approvedRefunds.Sum(item => item.Amount);
        foreach (var refund in approvedRefunds)
        {
            if (refund.Method == PaymentMethods.CompensationRefund &&
                string.IsNullOrWhiteSpace(refund.LedgerReference) &&
                CompensationLedger.IsFundedRefund(
                    refund.LedgerReference,
                    refund.TransactionCode))
            {
                refund.LedgerReference = refund.TransactionCode;
            }

            refund.Status = PaymentStatus.Refunded;
            refund.PaidAt = refundedAt;
            refund.TransactionCode = transactionCode;
        }

        var hasOpenRefund = booking.Payments.Any(item =>
            item.Type == PaymentType.Refund &&
            item.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved);
        if (booking.Status == BookingStatus.AwaitingRefund && !hasOpenRefund)
        {
            booking.Status = BookingStatus.Completed;
        }

        _dbContext.Notifications.Add(new Notification
        {
            UserId = booking.CustomerId,
            Title = "Hoàn tiền thành công",
            Message = $"Đơn #{booking.BookingId}: SmartCar đã hoàn tổng {total:N0} đồng vào {bank.BankName} - {bank.MaskedAccountNumber}. Mã giao dịch: {transactionCode}."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await WriteAuditAsync(
            "StaffExecuteApprovedRefund",
            nameof(Payment),
            bookingId,
            $"Thực hiện khoản hoàn đã được Admin duyệt cho đơn #{bookingId}: {total:N0} đồng, " +
            $"mã GD {transactionCode}; chuyển tới {bank.BankName} - {bank.MaskedAccountNumber}, " +
            $"chủ tài khoản {bank.AccountHolderName}.",
            cancellationToken);

        TempData["SuccessMessage"] = $"Đã hoàn {total:N0} đ cho đơn #{bookingId}.";
        return RedirectToAction(nameof(Refunds));
    }

    private async Task<HashSet<int>> GetOpenRefundBookingIdsAsync(CancellationToken cancellationToken) =>
        (await _dbContext.Payments.AsNoTracking()
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                (payment.Status == PaymentStatus.AwaitingRefund ||
                 payment.Status == PaymentStatus.RefundApproved))
            .Select(payment => payment.BookingId)
            .Distinct()
            .ToListAsync(cancellationToken))
        .ToHashSet();

    private async Task<bool> IsActiveCustomerAsync(
        string customerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            return false;

        var customerRoleId = await _dbContext.Roles.AsNoTracking()
            .Where(role => role.Name == RoleNames.Customer)
            .Select(role => role.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(customerRoleId))
            return false;

        return await _dbContext.Users.AsNoTracking().AnyAsync(user =>
            user.Id == customerId &&
            user.IsActive &&
            _dbContext.UserRoles.Any(userRole =>
                userRole.UserId == user.Id &&
                userRole.RoleId == customerRoleId),
            cancellationToken);
    }

    private static IReadOnlyList<string> FindSignedPaths(string? paths, string marker) =>
        string.IsNullOrWhiteSpace(paths)
            ? Array.Empty<string>()
            : paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .ToArray();

    private static string? ResolveStaffName(
        IReadOnlyDictionary<string, string> staffNames,
        string? staffId) =>
        !string.IsNullOrWhiteSpace(staffId) && staffNames.TryGetValue(staffId, out var name)
            ? name
            : null;

    private string CurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private Task WriteAuditAsync(
        string action,
        string entityName,
        int entityId,
        string description,
        CancellationToken cancellationToken) =>
        _auditService.WriteAsync(
            CurrentUserId(),
            action,
            entityName,
            entityId.ToString(),
            description,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
}
