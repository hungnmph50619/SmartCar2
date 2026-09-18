using System.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminPaymentsController : Controller
{
    private const string ForceMajeureMarker = "[FORCE_MAJEURE]";
    private const string LegacyDepositDeductionPrefix = "EXT-COMP-";

    private readonly IPaymentService _paymentService;
    private readonly IAuditService _auditService;
    private readonly ApplicationDbContext _dbContext;

    public AdminPaymentsController(
        IPaymentService paymentService,
        IAuditService auditService,
        ApplicationDbContext dbContext)
    {
        _paymentService = paymentService;
        _auditService = auditService;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        PaymentStatus? status,
        PaymentType? type,
        string? section,
        CancellationToken cancellationToken)
    {
        var reviewAll =
            string.IsNullOrWhiteSpace(section) &&
            !type.HasValue &&
            status == PaymentStatus.AwaitingConfirmation;

        section = reviewAll ? "review" : ResolveSection(section, type);

        ViewBag.Status = status;
        ViewBag.Type = type;
        ViewBag.Section = section;

        var payments = await _paymentService.GetAdminPaymentsAsync(
            status,
            type,
            cancellationToken);

        var filtered = payments.Where(payment => section switch
        {
            "review" =>
                payment.Status == PaymentStatus.AwaitingConfirmation &&
                payment.Type != PaymentType.Refund,
            "adjustment" =>
                payment.Status != PaymentStatus.Failed &&
                payment.Type is
                    PaymentType.Extension or
                    PaymentType.AdditionalCharge or
                    PaymentType.VehicleSwapAdjustment,
            "refund" =>
                payment.Type == PaymentType.Refund &&
                payment.Status != PaymentStatus.Failed,
            _ => payment.Type is PaymentType.Rental or PaymentType.Deposit
        }).ToList();

        return View(filtered);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRefundBatch(
        int bookingId,
        CancellationToken cancellationToken)
    {
        await RepairLegacyForceMajeureCompensationAsync(cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var booking = await _dbContext.Bookings
            .Include(item => item.VehicleReturn)
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId, cancellationToken);

        if (booking is null)
        {
            TempData["ErrorMessage"] = "Không tìm thấy đơn thuê cần hoàn tiền.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        var awaitingApproval = booking.Payments
            .Where(item =>
                item.Type == PaymentType.Refund &&
                item.Status == PaymentStatus.AwaitingRefund)
            .OrderBy(item => item.PaymentId)
            .ToList();

        if (awaitingApproval.Count == 0)
        {
            TempData["ErrorMessage"] =
                "Đơn này không còn khoản hoàn tiền nào đang chờ chủ/admin duyệt.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        if (awaitingApproval.Any(item => item.Amount <= 0))
        {
            TempData["ErrorMessage"] = "Có khoản hoàn tiền không hợp lệ. Vui lòng kiểm tra dữ liệu.";
            return RedirectToAction(nameof(Index), new { section = "refund" });
        }

        var hasDepositRefund = awaitingApproval.Any(item =>
            item.Method == PaymentMethods.DepositRefund);

        if (hasDepositRefund && booking.VehicleReturn is not null)
        {
            var holdDays = DepositHoldPolicy.NormalizeDays(booking.DepositHoldDaysApplied);
            var eligibleAt = DepositHoldPolicy.CalculateEligibleAt(
                booking.VehicleReturn.ReturnedAt,
                holdDays);
            var vietnamNow = DateTime.UtcNow.AddHours(7);

            if (vietnamNow < eligibleAt)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] =
                    $"Booking #{booking.BookingId} áp dụng chính sách giữ cọc {holdDays} ngày. " +
                    $"Sớm nhất được duyệt hoàn lúc {eligibleAt:dd/MM/yyyy HH:mm}.";
                return RedirectToAction(nameof(Index), new { section = "refund" });
            }

            var outstandingTrafficFine = booking.Payments
                .Where(payment =>
                    payment.Type == PaymentType.TrafficFine &&
                    payment.Status is PaymentStatus.Pending or PaymentStatus.AwaitingConfirmation or PaymentStatus.Failed)
                .Sum(payment => payment.Amount);

            if (outstandingTrafficFine > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] =
                    $"Booking #{booking.BookingId} còn {outstandingTrafficFine:N0} đồng phạt/vi phạm chưa xử lý. " +
                    "Cần đối soát khoản này trước khi duyệt hoàn cọc.";
                return RedirectToAction(nameof(Index), new { section = "refund" });
            }
        }

        foreach (var refund in awaitingApproval)
        {
            refund.Status = PaymentStatus.RefundApproved;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var totalRefund = awaitingApproval.Sum(item => item.Amount);
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        await _auditService.WriteAsync(
            adminId,
            "ApproveRefundBatch",
            nameof(Payment),
            booking.BookingId.ToString(),
            $"Chủ/admin duyệt hoàn tiền cho đơn #{booking.BookingId}: tổng {totalRefund:N0} đồng. Chờ nhân viên thực hiện.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã duyệt hoàn {totalRefund:N0} đ cho đơn #{booking.BookingId}. Nhân viên sẽ thực hiện chuyển tiền.";

        return RedirectToAction(nameof(Index), new { section = "refund" });
    }

    // Giữ action cũ để tránh liên kết cũ gây 404; Admin không còn quyền tự đánh dấu đã hoàn.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ConfirmRefundBatch(int bookingId)
    {
        TempData["ErrorMessage"] =
            "Quy trình mới: chủ/Admin chỉ duyệt khoản hoàn. Nhân viên thực hiện chuyển tiền và nhập mã giao dịch.";
        return RedirectToAction(nameof(Index), new { section = "refund" });
    }

    private async Task RepairLegacyForceMajeureCompensationAsync(
        CancellationToken cancellationToken)
    {
        var legacyExtensions = await _dbContext.BookingExtensions
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.Payments)
            .Include(item => item.Booking)
                .ThenInclude(booking => booking.VehicleReturn)
                    .ThenInclude(vehicleReturn => vehicleReturn!.AdditionalCharges)
            .Where(item =>
                item.CustomerNote != null &&
                item.CustomerNote.Contains(ForceMajeureMarker) &&
                item.CustomerNote.Contains(CompensationLedger.Marker))
            .ToListAsync(cancellationToken);

        if (legacyExtensions.Count == 0)
        {
            return;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        foreach (var extension in legacyExtensions)
        {
            var reservations = ParseLegacyCompensations(extension.CustomerNote);
            var currentBooking = extension.Booking;

            var legacyDeductions = currentBooking.Payments
                .Where(payment =>
                    payment.Type == PaymentType.AdditionalCharge &&
                    payment.Method == PaymentMethods.DepositDeduction &&
                    payment.Status == PaymentStatus.Paid &&
                    !string.IsNullOrWhiteSpace(payment.TransactionCode) &&
                    payment.TransactionCode.StartsWith(
                        $"{LegacyDepositDeductionPrefix}{currentBooking.BookingId}-",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var deduction in legacyDeductions)
            {
                deduction.Status = PaymentStatus.Failed;
            }

            if (legacyDeductions.Count > 0 && currentBooking.VehicleReturn is not null)
            {
                var actualAdditionalAmount = currentBooking.VehicleReturn.AdditionalCharges.Sum(charge => charge.Amount);
                var staleAdditionalAmount = Math.Min(
                    legacyDeductions.Sum(payment => payment.Amount),
                    Math.Max(0m, currentBooking.AdditionalAmount - actualAdditionalAmount));

                if (staleAdditionalAmount > 0)
                {
                    currentBooking.AdditionalAmount = Math.Max(
                        actualAdditionalAmount,
                        currentBooking.AdditionalAmount - staleAdditionalAmount);
                    currentBooking.TotalAmount = Math.Max(
                        0m,
                        currentBooking.TotalAmount - staleAdditionalAmount);
                }
            }

            foreach (var reservation in reservations)
            {
                var affectedBooking = await _dbContext.Bookings
                    .Include(item => item.Payments)
                    .FirstOrDefaultAsync(item => item.BookingId == reservation.BookingId, cancellationToken);

                if (affectedBooking is null)
                {
                    continue;
                }

                var pendingAutomaticCompensation = affectedBooking.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.Refund &&
                        payment.Method == PaymentMethods.CompensationRefund &&
                        payment.Status == PaymentStatus.AwaitingRefund &&
                        payment.Amount == reservation.Amount)
                    .OrderByDescending(payment => payment.PaymentId)
                    .FirstOrDefault();

                if (pendingAutomaticCompensation is not null)
                {
                    pendingAutomaticCompensation.Status = PaymentStatus.Failed;
                    affectedBooking.RefundAmount = affectedBooking.Payments
                        .Where(payment =>
                            payment.Type == PaymentType.Refund &&
                            BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
                        .Sum(payment => payment.Amount);
                    affectedBooking.RefundReason = AppendText(
                        affectedBooking.RefundReason,
                        "Đã hủy khoản hỗ trợ tự động cũ: trường hợp bất khả kháng chỉ hoàn các khoản khách đã thanh toán; không tự động lấy cọc khách A để bồi thường.");
                }
            }

            var paidDeposit = currentBooking.Payments
                .Where(payment =>
                    payment.Type == PaymentType.Deposit &&
                    payment.Status == PaymentStatus.Paid)
                .Sum(payment => payment.Amount);

            var depositRefundedOrPlanned = currentBooking.Payments
                .Where(payment =>
                    payment.Type == PaymentType.Refund &&
                    payment.Method == PaymentMethods.DepositRefund &&
                    payment.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded)
                .Sum(payment => payment.Amount);

            var validDepositDeductions = currentBooking.Payments
                .Where(payment =>
                    payment.Type == PaymentType.AdditionalCharge &&
                    payment.Method == PaymentMethods.DepositDeduction &&
                    payment.Status == PaymentStatus.Paid &&
                    (string.IsNullOrWhiteSpace(payment.TransactionCode) ||
                     !payment.TransactionCode.StartsWith(
                         LegacyDepositDeductionPrefix,
                         StringComparison.OrdinalIgnoreCase)))
                .Sum(payment => payment.Amount);

            var depositCorrection = Math.Max(
                0m,
                paidDeposit - depositRefundedOrPlanned - validDepositDeductions);

            if (legacyDeductions.Count > 0 && depositCorrection > 0)
            {
                currentBooking.Payments.Add(new Payment
                {
                    Type = PaymentType.Refund,
                    Amount = depositCorrection,
                    Method = PaymentMethods.DepositRefund,
                    Status = PaymentStatus.AwaitingRefund
                });
                currentBooking.RefundAmount = currentBooking.Payments
                    .Where(payment =>
                        payment.Type == PaymentType.Refund &&
                        BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
                    .Sum(payment => payment.Amount);
                currentBooking.RefundReason = AppendText(
                    currentBooking.RefundReason,
                    $"Điều chỉnh chính sách bất khả kháng: hoàn bổ sung {depositCorrection:N0} đồng cọc; không áp dụng khoản khấu trừ tự động cho đơn kế tiếp.");

                if (currentBooking.Status == BookingStatus.Completed)
                {
                    currentBooking.Status = BookingStatus.AwaitingRefund;
                }

                _dbContext.Notifications.Add(new Notification
                {
                    UserId = currentBooking.CustomerId,
                    Title = "Điều chỉnh hoàn tiền cọc",
                    Message =
                        $"Đơn #{currentBooking.BookingId}: SmartCar đã bỏ khoản khấu trừ cọc tự động của luồng bất khả kháng cũ. " +
                        $"Có thêm {depositCorrection:N0} đồng cọc đang chờ hoàn."
                });
            }

            extension.CustomerNote = RemoveLegacyCompensationMarkers(extension.CustomerNote);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        await _auditService.WriteAsync(
            adminId,
            "RepairForceMajeureCompensation",
            nameof(Payment),
            "legacy",
            "Đã tự động điều chỉnh các bản ghi thử nghiệm cũ: hủy bồi thường tự động chưa chuyển và hoàn bổ sung cọc A nếu trước đây bị khấu trừ do gia hạn bất khả kháng.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<(decimal Amount, int BookingId)> ParseLegacyCompensations(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<(decimal, int)>();
        }

        const string bookingMarker = "BOOKING:";
        var result = new List<(decimal Amount, int BookingId)>();
        var searchIndex = 0;

        while (searchIndex < value.Length)
        {
            var markerIndex = value.IndexOf(CompensationLedger.Marker, searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                break;
            }

            var amountStart = markerIndex + CompensationLedger.Marker.Length;
            var separatorIndex = value.IndexOf('|', amountStart);
            if (separatorIndex < 0)
            {
                break;
            }

            var bookingMarkerIndex = value.IndexOf(bookingMarker, separatorIndex + 1, StringComparison.Ordinal);
            if (bookingMarkerIndex < 0)
            {
                break;
            }

            var bookingStart = bookingMarkerIndex + bookingMarker.Length;
            var bookingEnd = value.IndexOfAny(new[] { '\r', '\n' }, bookingStart);
            var amountText = value[amountStart..separatorIndex].Trim();
            var bookingText = (bookingEnd < 0 ? value[bookingStart..] : value[bookingStart..bookingEnd]).Trim();

            if (decimal.TryParse(
                    amountText,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var amount) &&
                amount > 0 &&
                int.TryParse(bookingText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bookingId) &&
                bookingId > 0)
            {
                result.Add((amount, bookingId));
            }

            searchIndex = bookingEnd < 0 ? value.Length : bookingEnd + 1;
        }

        return result;
    }

    private static string? RemoveLegacyCompensationMarkers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var lines = value
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Contains(CompensationLedger.Marker, StringComparison.Ordinal))
            .ToArray();

        return lines.Length == 0 ? null : string.Join('\n', lines);
    }

    private static string AppendText(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current)
            ? addition
            : $"{current.Trim()} {addition}";

    private static string RefundPurposeText(string? method) => method switch
    {
        PaymentMethods.DepositRefund => "Hoàn cọc",
        PaymentMethods.VehicleSwapRefund => "Hoàn chênh lệch đổi xe",
        PaymentMethods.CompensationRefund => "Hỗ trợ/bồi thường",
        PaymentMethods.BankTransferRefund => "Hoàn tiền thuê/phí giao",
        _ => "Hoàn tiền"
    };

    private static string ResolveSection(string? section, PaymentType? type)
    {
        if (section is "collection" or "adjustment" or "refund" or "review")
        {
            return section;
        }

        return type switch
        {
            PaymentType.Extension or
            PaymentType.AdditionalCharge or
            PaymentType.VehicleSwapAdjustment => "adjustment",
            PaymentType.Refund => "refund",
            _ => "collection"
        };
    }
}
