using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class PaymentsController : Controller
{
    private const string HandoverSignedMarker = "signed-handover-";
    private const string ReturnSignedMarker = "signed-return-";

    private readonly IPaymentService _paymentService;
    private readonly ApplicationDbContext _dbContext;

    public PaymentsController(
        IPaymentService paymentService,
        ApplicationDbContext dbContext)
    {
        _paymentService = paymentService;
        _dbContext = dbContext;
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

        if (type == PaymentType.AdditionalCharge)
        {
            var ownsBooking = await _dbContext.Bookings
                .AsNoTracking()
                .AnyAsync(
                    booking => booking.BookingId == bookingId && booking.CustomerId == customerId,
                    cancellationToken);

            if (!ownsBooking)
            {
                return NotFound();
            }

            var handoverVerified = await _dbContext.VehicleHandovers
                .AsNoTracking()
                .AnyAsync(
                    handover =>
                        handover.BookingId == bookingId &&
                        handover.CustomerIdentityVerified &&
                        handover.SignedDocumentVerified &&
                        handover.ImagePaths != null &&
                        handover.ImagePaths.Contains(HandoverSignedMarker),
                    cancellationToken);

            var returnVerified = await _dbContext.VehicleReturns
                .AsNoTracking()
                .AnyAsync(
                    vehicleReturn =>
                        vehicleReturn.BookingId == bookingId &&
                        vehicleReturn.CustomerIdentityVerified &&
                        vehicleReturn.SignedDocumentVerified &&
                        vehicleReturn.ImagePaths != null &&
                        vehicleReturn.ImagePaths.Contains(ReturnSignedMarker),
                    cancellationToken);

            if (!handoverVerified || !returnVerified)
            {
                TempData["ErrorMessage"] =
                    "Phụ phí chưa được nhân viên chốt hồ sơ giao - trả. Vui lòng chờ SmartCar kiểm tra và xác minh biên bản trước khi thanh toán.";

                return RedirectToAction(
                    "Details",
                    "Bookings",
                    new { id = bookingId });
            }
        }

        var result = await _paymentService.SubmitQrPaymentAsync(
            bookingId,
            customerId,
            type,
            customerId,
            cancellationToken);

        if (result.Succeeded && type == PaymentType.Rental)
        {
            var reconciliationExpiresAt = DateTime.UtcNow
                .AddMinutes(RentalPolicy.BookingTransferReconciliationHoldMinutes);
            await _dbContext.Bookings
                .Where(booking =>
                    booking.BookingId == bookingId &&
                    booking.CustomerId == customerId &&
                    booking.Status == BookingStatus.PendingPayment)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        booking => booking.ReservationExpiresAt,
                        reconciliationExpiresAt),
                    cancellationToken);
        }

        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? type == PaymentType.Rental
                ? $"Đã báo chuyển khoản. SmartCar có tối đa {RentalPolicy.BookingTransferReconciliationHoldMinutes} phút để đối soát; trạng thái này không giữ xe vô thời hạn."
                : "Đã gửi thông tin chuyển khoản. Vui lòng chờ SmartCar xác nhận."
            : string.Join("; ", result.Errors);

        return RedirectToAction(
            "Details",
            "Bookings",
            new { id = bookingId });
    }
}
