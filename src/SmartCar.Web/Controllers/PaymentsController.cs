using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class PaymentsController : Controller
{
    private readonly IPaymentService _paymentService;

    public PaymentsController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(
        int bookingId,
        PaymentType type,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var result = await _paymentService.SimulatePaymentAsync(
            bookingId,
            customerId,
            type,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? type == PaymentType.Rental
                ? "Thanh toán mô phỏng một lần cho tiền thuê/phí giao nhận và cọc bảo đảm thành công."
                : "Thanh toán mô phỏng thành công."
            : string.Join("; ", result.Errors);

        return RedirectToAction("Details", "Bookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitQr(
        int bookingId,
        PaymentType type,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return Challenge();
        }

        var result = await _paymentService.SubmitQrPaymentAsync(
            bookingId,
            customerId,
            type,
            cancellationToken);

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? type == PaymentType.Rental
                ? "Đã gửi xác nhận giao dịch một lần gồm tiền thuê/phí giao nhận và cọc bảo đảm. Vui lòng chờ SmartCar đối soát."
                : "Đã gửi thông tin chuyển khoản. Vui lòng chờ SmartCar xác nhận."
            : string.Join("; ", result.Errors);

        return RedirectToAction(
            "Details",
            "Bookings",
            new { id = bookingId });
    }
}
