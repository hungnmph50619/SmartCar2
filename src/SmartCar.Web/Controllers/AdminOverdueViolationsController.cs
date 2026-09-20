using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminOverdueViolationsController : Controller
{
    private static readonly BookingStatus[] BlockingStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IBookingOperationService _bookingOperationService;
    private readonly IAuditService _auditService;

    public AdminOverdueViolationsController(
        ApplicationDbContext dbContext,
        IBookingOperationService bookingOperationService,
        IAuditService auditService)
    {
        _dbContext = dbContext;
        _bookingOperationService = bookingOperationService;
        _auditService = auditService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Process(
        int renterBookingId,
        int affectedBookingId,
        bool violationConfirmed,
        CancellationToken cancellationToken)
    {
        IActionResult Back() => RedirectToAction("Details", "AdminBookings", new { id = renterBookingId });

        if (!violationConfirmed)
        {
            TempData["ErrorMessage"] = "Bạn phải xác nhận vi phạm trước khi xử lý.";
            return Back();
        }

        var renter = await _dbContext.Bookings
            .Include(x => x.Payments)
            .Include(x => x.VehicleReturn)
            .FirstOrDefaultAsync(x => x.BookingId == renterBookingId, cancellationToken);
        var affected = await _dbContext.Bookings
            .Include(x => x.Payments)
            .FirstOrDefaultAsync(x => x.BookingId == affectedBookingId, cancellationToken);

        if (renter is null || affected is null || renter.VehicleId != affected.VehicleId)
        {
            TempData["ErrorMessage"] = "Không tìm thấy hai đơn hợp lệ dùng cùng một xe.";
            return Back();
        }

        var rejected = await _dbContext.BookingExtensions.AsNoTracking().AnyAsync(x =>
            x.BookingId == renterBookingId && x.Status == BookingExtensionStatus.Rejected,
            cancellationToken);
        if (!rejected)
        {
            TempData["ErrorMessage"] = "Đơn đang thuê chưa có yêu cầu gia hạn bị từ chối.";
            return Back();
        }

        if (renter.Status != BookingStatus.Rented || DateTime.Now <= renter.ReturnDate || DateTime.Now < affected.PickupDate)
        {
            TempData["ErrorMessage"] = "Chưa đủ điều kiện: xe phải đang bị giữ quá hạn và đơn kế tiếp đã đến giờ nhận.";
            return Back();
        }

        var duplicateDeductionPrefix =
            $"{OverdueCompensationLedger.DepositDeductionPrefix}{renterBookingId}-{affectedBookingId}-";
        var duplicateDebtPrefix =
            $"{OverdueCompensationLedger.DebtPrefix}{renterBookingId}-{affectedBookingId}-";

        if (renter.Payments.Any(payment =>
                !string.IsNullOrWhiteSpace(payment.TransactionCode) &&
                (payment.TransactionCode.StartsWith(
                     duplicateDeductionPrefix,
                     StringComparison.OrdinalIgnoreCase) ||
                 payment.TransactionCode.StartsWith(
                     duplicateDebtPrefix,
                     StringComparison.OrdinalIgnoreCase))))
        {
            TempData["ErrorMessage"] =
                "Vi phạm giữa hai đơn này đã được xử lý trước đó; không tạo thêm khấu trừ, nợ hoặc bồi thường trùng.";
            return Back();
        }

        var contractCompensation = Math.Max(0m, affected.TotalAmount);
        if (contractCompensation <= 0)
        {
            TempData["ErrorMessage"] = "Giá trị hợp đồng của đơn bị ảnh hưởng không hợp lệ.";
            return Back();
        }

        var availableDeposit = CalculateAvailableDeposit(renter.Payments);
        var depositDeduction = Math.Min(availableDeposit, contractCompensation);
        var outstanding = Math.Max(0m, contractCompensation - depositDeduction);
        var now = DateTime.UtcNow;

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        if (BlockingStatuses.Contains(affected.Status))
        {
            var cancelResult = await _bookingOperationService.CancelByAdminAsync(
                adminId,
                new CancelBookingRequest(affectedBookingId,
                    $"Không thể giao xe vì đơn #{renterBookingId} đã bị từ chối gia hạn nhưng vẫn giữ xe quá hạn."),
                cancellationToken);
            if (!cancelResult.Succeeded)
            {
                TempData["ErrorMessage"] = string.Join("; ", cancelResult.Errors);
                return Back();
            }

            affected = await _dbContext.Bookings.Include(x => x.Payments)
                .FirstAsync(x => x.BookingId == affectedBookingId, cancellationToken);
        }

        if (depositDeduction > 0)
        {
            renter.Payments.Add(new Payment
            {
                Type = PaymentType.AdditionalCharge,
                Amount = depositDeduction,
                Method = PaymentMethods.DepositDeduction,
                Status = PaymentStatus.Paid,
                PaidAt = now,
                TransactionCode = OverdueCompensationLedger.BuildDepositDeductionCode(
                    renterBookingId,
                    affectedBookingId,
                    now)
            });
        }

        if (outstanding > 0)
        {
            renter.Payments.Add(new Payment
            {
                Type = PaymentType.OverdueCompensationDebt,
                Amount = outstanding,
                Method = PaymentMethods.NotSelected,
                Status = PaymentStatus.Pending,
                TransactionCode = OverdueCompensationLedger.BuildDebtCode(
                    renterBookingId,
                    affectedBookingId,
                    now)
            });
        }

        // Chỉ tạo khoản bồi thường cho B bằng phần đã có nguồn thực tế từ cọc A.
        // Phần còn thiếu nằm ở OverdueCompensationDebt và chỉ trở thành Refund của B
        // sau khi A thanh toán/được Staff đối soát thành công.
        if (depositDeduction > 0m)
        {
            var fundedRefundReference =
                OverdueCompensationLedger.BuildFundedRefundCode(
                    renterBookingId,
                    affectedBookingId,
                    now);

            affected.Payments.Add(new Payment
            {
                Type = PaymentType.Refund,
                Amount = depositDeduction,
                Method = PaymentMethods.CompensationRefund,
                Status = PaymentStatus.AwaitingRefund,
                TransactionCode = fundedRefundReference,
                LedgerReference = fundedRefundReference
            });
        }

        affected.RefundAmount = affected.Payments
            .Where(payment =>
                payment.Type == PaymentType.Refund &&
                BookingWorkflowRules.CountsTowardRefundTotal(payment.Status))
            .Sum(payment => payment.Amount);
        affected.RefundReason = AppendText(
            affected.RefundReason,
            $"Bồi thường do đơn #{renterBookingId} giữ xe quá hạn: tổng nghĩa vụ {contractCompensation:N0} đồng; " +
            $"đã có nguồn từ cọc {depositDeduction:N0} đồng" +
            (outstanding > 0m
                ? $"; còn {outstanding:N0} đồng chỉ tạo khoản hoàn bổ sung sau khi thực thu từ khách vi phạm."
                : "; đã đủ nguồn."));

        _dbContext.Notifications.Add(new Notification
        {
            UserId = renter.CustomerId,
            Title = "Xử lý vi phạm trả xe quá hạn",
            Message = $"Đơn #{renterBookingId}: mức bồi thường {contractCompensation:N0} đ; tạm khấu trừ {depositDeduction:N0} đ từ cọc đang giữ" +
                      (outstanding > 0 ? $" và còn phải thanh toán thêm {outstanding:N0} đ." : ".") +
                      " Cọc còn lại chỉ được quyết toán sau khi trả và kiểm tra xe."
        });
        _dbContext.Notifications.Add(new Notification
        {
            UserId = affected.CustomerId,
            Title = "Hoàn tiền và bồi thường do không thể giao xe",
            Message =
                $"Đơn #{affectedBookingId}: tổng mức bồi thường được ghi nhận là {contractCompensation:N0} đ. " +
                $"Hiện đã có nguồn {depositDeduction:N0} đ từ cọc khách gây ảnh hưởng" +
                (outstanding > 0m
                    ? $"; {outstanding:N0} đ còn lại sẽ chuyển sang khoản hoàn khi SmartCar thực thu được từ khách đó."
                    : " và đã đủ nguồn bồi thường.") +
                " Các khoản tiền bạn đã thanh toán cho đơn bị hủy vẫn được xử lý hoàn độc lập."
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _auditService.WriteAsync(adminId, "ProcessOverdueVehicleConflict", nameof(Booking), renterBookingId.ToString(),
            $"Đơn #{renterBookingId} ảnh hưởng #{affectedBookingId}. Bồi thường {contractCompensation:N0}; khấu trừ cọc {depositDeduction:N0}; còn phải thu {outstanding:N0}.",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = outstanding > 0
            ? $"Đã xử lý: bồi thường {contractCompensation:N0} đ; giữ/khấu trừ {depositDeduction:N0} đ từ cọc; còn phải thu khách #{renterBookingId} {outstanding:N0} đ."
            : $"Đã xử lý: bồi thường {contractCompensation:N0} đ được khấu trừ từ cọc khách #{renterBookingId}.";
        return Back();
    }

    private static decimal CalculateAvailableDeposit(IEnumerable<Payment> payments)
    {
        var paid = payments.Where(x => x.Type == PaymentType.Deposit && x.Status == PaymentStatus.Paid).Sum(x => x.Amount);
        var refunded = payments.Where(x => x.Type == PaymentType.Refund && x.Method == PaymentMethods.DepositRefund && x.Status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved or PaymentStatus.Refunded).Sum(x => x.Amount);
        var deducted = payments.Where(x => x.Type == PaymentType.AdditionalCharge && x.Method == PaymentMethods.DepositDeduction && x.Status == PaymentStatus.Paid).Sum(x => x.Amount);
        return Math.Max(0m, paid - refunded - deducted);
    }

    private static string AppendText(string? current, string addition)
    {
        var combined = string.IsNullOrWhiteSpace(current) ? addition : $"{current.Trim()} {addition}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }
}