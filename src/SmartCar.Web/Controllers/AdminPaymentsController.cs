using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Web.Services;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
public sealed class AdminPaymentsController : Controller
{
    private readonly IPaymentService _paymentService;
    private readonly IUserBankAccountService _bankAccountService;
    private readonly IAuditService _auditService;

    public AdminPaymentsController(
        IPaymentService paymentService,
        IUserBankAccountService bankAccountService,
        IAuditService auditService)
    {
        _paymentService = paymentService;
        _bankAccountService = bankAccountService;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        PaymentStatus? status,
        PaymentType? type,
        CancellationToken cancellationToken)
    {
        ViewBag.Status = status;
        ViewBag.Type = type;

        var payments = await _paymentService.GetAdminPaymentsAsync(
            status,
            type,
            cancellationToken);

        // Link cũ từ trang chi tiết đơn từng mở thẳng danh sách DepositRefund.
        // Nếu chỉ có đúng một đơn đang chờ hoàn cọc, chuyển sang màn hoàn tiền theo đơn
        // để Admin nhìn thấy cả phần hoàn tiền chuyến và phần hoàn cọc trong cùng một nghiệp vụ.
        if (status == PaymentStatus.AwaitingRefund &&
            type == PaymentType.DepositRefund &&
            payments.Count == 1)
        {
            return RedirectToAction(
                nameof(BookingRefund),
                new { bookingId = payments[0].BookingId });
        }

        return View(payments);
    }

    [HttpGet]
    public async Task<IActionResult> BookingRefund(
        int bookingId,
        CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetAdminPaymentsAsync(
            status: null,
            type: null,
            cancellationToken: cancellationToken);

        var bookingPayments = payments
            .Where(item => item.BookingId == bookingId)
            .OrderBy(item => item.PaymentId)
            .ToList();

        if (bookingPayments.Count == 0 ||
            !bookingPayments.Any(item =>
                item.Type is PaymentType.Refund or PaymentType.DepositRefund))
        {
            TempData["ErrorMessage"] = "Đơn này chưa có khoản hoàn tiền hoặc hoàn cọc để xử lý.";
            return RedirectToAction(nameof(Index), new { status = PaymentStatus.AwaitingRefund });
        }

        return View(bookingPayments);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmBookingRefund(
        int bookingId,
        string transactionCode,
        bool transferCompleted,
        CancellationToken cancellationToken)
    {
        if (!transferCompleted)
        {
            TempData["ErrorMessage"] = "Chỉ xác nhận sau khi đã thực tế chuyển tổng khoản hoàn cho khách.";
            return RedirectToAction(nameof(BookingRefund), new { bookingId });
        }

        var normalizedTransactionCode = transactionCode?.Trim() ?? string.Empty;
        if (normalizedTransactionCode.Length < 4 || normalizedTransactionCode.Length > 100)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập mã giao dịch ngân hàng thực tế sau khi chuyển tiền (4-100 ký tự).";
            return RedirectToAction(nameof(BookingRefund), new { bookingId });
        }

        var allPayments = await _paymentService.GetAdminPaymentsAsync(
            status: null,
            type: null,
            cancellationToken: cancellationToken);

        var pendingRefunds = allPayments
            .Where(item =>
                item.BookingId == bookingId &&
                item.Type is PaymentType.Refund or PaymentType.DepositRefund &&
                item.Status == PaymentStatus.AwaitingRefund)
            .OrderBy(item => item.PaymentId)
            .ToList();

        if (pendingRefunds.Count == 0)
        {
            TempData["ErrorMessage"] = "Đơn này không còn khoản hoàn nào ở trạng thái chờ xử lý.";
            return RedirectToAction(nameof(BookingRefund), new { bookingId });
        }

        var customerId = pendingRefunds[0].CustomerId;
        if (pendingRefunds.Any(item => !string.Equals(item.CustomerId, customerId, StringComparison.Ordinal)))
        {
            TempData["ErrorMessage"] = "Dữ liệu người nhận của các khoản hoàn không đồng nhất. Vui lòng kiểm tra lại.";
            return RedirectToAction(nameof(BookingRefund), new { bookingId });
        }

        var refundBankAccount = await _bankAccountService.GetDefaultAsync(
            customerId,
            cancellationToken);
        if (refundBankAccount is null)
        {
            TempData["ErrorMessage"] = "Khách chưa có tài khoản ngân hàng mặc định để nhận khoản hoàn.";
            return RedirectToAction(nameof(BookingRefund), new { bookingId });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var totalRefund = pendingRefunds.Sum(item => item.Amount);
        var tripRefund = pendingRefunds
            .Where(item => item.Type == PaymentType.Refund)
            .Sum(item => item.Amount);
        var depositRefund = pendingRefunds
            .Where(item => item.Type == PaymentType.DepositRefund)
            .Sum(item => item.Amount);

        // Một giao dịch ngân hàng có thể bao gồm nhiều cấu phần kế toán của cùng đơn.
        // Xác nhận tuần tự cùng một mã giao dịch để hai Payment vẫn được lưu riêng,
        // nhưng Admin chỉ cần thực hiện một lần chuyển tổng cho khách.
        foreach (var refund in pendingRefunds)
        {
            var result = await _paymentService.ConfirmRefundAsync(
                refund.PaymentId,
                adminId,
                normalizedTransactionCode,
                cancellationToken);

            if (!result.Succeeded)
            {
                TempData["ErrorMessage"] =
                    "Không thể hoàn tất toàn bộ khoản hoàn của đơn. " +
                    string.Join("; ", result.Errors);
                return RedirectToAction(nameof(BookingRefund), new { bookingId });
            }
        }

        var transferContent = $"SC{bookingId}-REFUND-TOTAL";
        await _auditService.WriteAsync(
            adminId,
            "BookingRefundEvidence",
            "Booking",
            bookingId.ToString(),
            $"Xác nhận đã chuyển tổng khoản hoàn {totalRefund:N0} đồng cho đơn #{bookingId}: " +
            $"hoàn phần tiền chuyến {tripRefund:N0} đồng, hoàn cọc {depositRefund:N0} đồng. " +
            $"Người nhận: {refundBankAccount.AccountHolderName}; ngân hàng: {refundBankAccount.BankName}; " +
            $"tài khoản: {refundBankAccount.MaskedAccountNumber}; nội dung chuyển: {transferContent}; " +
            $"mã giao dịch ngân hàng: {normalizedTransactionCode}.",
            newValues:
                $"BookingId={bookingId};TotalRefund={totalRefund};TripRefund={tripRefund};DepositRefund={depositRefund};" +
                $"Bank={refundBankAccount.BankName};Account={refundBankAccount.MaskedAccountNumber};" +
                $"Holder={refundBankAccount.AccountHolderName};TransferContent={transferContent};" +
                $"BankTransactionCode={normalizedTransactionCode}",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] =
            $"Đã ghi nhận chuyển tổng {totalRefund:N0} đ cho đơn #{bookingId}, gồm hoàn tiền chuyến {tripRefund:N0} đ và hoàn cọc {depositRefund:N0} đ.";

        return RedirectToAction(nameof(BookingRefund), new { bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmQr(
        int paymentId,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        var result = await _paymentService.ConfirmQrPaymentAsync(
            paymentId,
            adminId,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã xác nhận nhận được tiền."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectQr(
        int paymentId,
        string rejectionReason,
        CancellationToken cancellationToken)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        var result = await _paymentService.RejectQrPaymentAsync(
            paymentId,
            adminId,
            rejectionReason,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "Đã ghi nhận chưa tìm thấy giao dịch. Mã yêu cầu và lý do đối soát vẫn được lưu trong lịch sử."
            : string.Join("; ", result.Errors);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmRefund(
        int paymentId,
        string transactionCode,
        bool transferCompleted,
        CancellationToken cancellationToken)
    {
        if (!transferCompleted)
        {
            TempData["ErrorMessage"] = "Chỉ xác nhận sau khi đã thực tế chuyển tiền cho khách.";
            return RedirectToAction(nameof(Index), new { status = PaymentStatus.AwaitingRefund });
        }

        var normalizedTransactionCode = transactionCode?.Trim() ?? string.Empty;
        if (normalizedTransactionCode.Length < 4 || normalizedTransactionCode.Length > 100)
        {
            TempData["ErrorMessage"] = "Vui lòng nhập mã giao dịch ngân hàng thực tế sau khi chuyển tiền (4-100 ký tự).";
            return RedirectToAction(nameof(Index), new { status = PaymentStatus.AwaitingRefund });
        }

        var payments = await _paymentService.GetAdminPaymentsAsync(
            status: null,
            type: null,
            cancellationToken: cancellationToken);
        var payment = payments.FirstOrDefault(item => item.PaymentId == paymentId);

        if (payment is null ||
            payment.Type is not (PaymentType.Refund or PaymentType.DepositRefund) ||
            payment.Status != PaymentStatus.AwaitingRefund)
        {
            TempData["ErrorMessage"] = "Khoản hoàn không còn ở trạng thái chờ xử lý.";
            return RedirectToAction(nameof(Index), new { status = PaymentStatus.AwaitingRefund });
        }

        var refundBankAccount = await _bankAccountService.GetDefaultAsync(
            payment.CustomerId,
            cancellationToken);
        if (refundBankAccount is null)
        {
            TempData["ErrorMessage"] = "Khách chưa có tài khoản ngân hàng mặc định để nhận khoản hoàn.";
            return RedirectToAction(nameof(Index), new { status = PaymentStatus.AwaitingRefund });
        }

        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var result = await _paymentService.ConfirmRefundAsync(
            paymentId,
            adminId,
            normalizedTransactionCode,
            cancellationToken);

        if (!result.Succeeded)
        {
            TempData["ErrorMessage"] = string.Join("; ", result.Errors);
            return RedirectToAction(nameof(Index), new { status = PaymentStatus.AwaitingRefund });
        }

        var isDepositRefund = payment.Type == PaymentType.DepositRefund;
        var transferContent = isDepositRefund
            ? $"SC{payment.BookingId}-DEPOSIT-REFUND"
            : $"SC{payment.BookingId}-REFUND";
        var evidenceDescription =
            $"Xác nhận {(isDepositRefund ? "hoàn cọc" : "hoàn tiền")} {payment.Amount:N0} đồng cho đơn #{payment.BookingId}. " +
            $"Người nhận: {refundBankAccount.AccountHolderName}; ngân hàng: {refundBankAccount.BankName}; " +
            $"tài khoản: {refundBankAccount.MaskedAccountNumber}; nội dung chuyển: {transferContent}; " +
            $"mã giao dịch ngân hàng: {normalizedTransactionCode}.";

        await _auditService.WriteAsync(
            adminId,
            isDepositRefund ? "DepositRefundEvidence" : "RefundEvidence",
            "Payment",
            payment.PaymentId.ToString(),
            evidenceDescription,
            newValues:
                $"BookingId={payment.BookingId};Amount={payment.Amount};Bank={refundBankAccount.BankName};" +
                $"Account={refundBankAccount.MaskedAccountNumber};Holder={refundBankAccount.AccountHolderName};" +
                $"TransferContent={transferContent};BankTransactionCode={normalizedTransactionCode}",
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        TempData["SuccessMessage"] = isDepositRefund
            ? $"Đã ghi nhận hoàn cọc {payment.Amount:N0} đ. Mã giao dịch ngân hàng: {normalizedTransactionCode}."
            : $"Đã ghi nhận hoàn tiền {payment.Amount:N0} đ. Mã giao dịch ngân hàng: {normalizedTransactionCode}.";

        return RedirectToAction(nameof(Index), new { status = PaymentStatus.Refunded });
    }
}
