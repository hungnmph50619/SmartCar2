using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Bookings;
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
    public async Task<IActionResult> ChooseUpfrontMethod(
        int bookingId,
        string method,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(customerId)) return Challenge();

        if (method is not ("cash" or "qr")) return BadRequest();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var booking = await _dbContext.Bookings
            .Include(item => item.Payments)
            .FirstOrDefaultAsync(item => item.BookingId == bookingId &&
                item.CustomerId == customerId, cancellationToken);
        if (booking is null) return NotFound();

        var rental = booking.Payments.FirstOrDefault(item =>
            item.Type == PaymentType.Rental && item.Status == PaymentStatus.Pending);
        if (booking.Status != BookingStatus.PendingPayment || rental is null ||
            booking.Payments.Any(item =>
                (item.Type is PaymentType.Rental or PaymentType.Deposit) &&
                item.Status == PaymentStatus.AwaitingConfirmation) ||
            (booking.ReservationExpiresAt.HasValue && booking.ReservationExpiresAt <= DateTime.UtcNow))
        {
            TempData["ErrorMessage"] = "Đơn không còn ở bước chọn cách thanh toán hoặc đã hết hạn giữ xe.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var cashCutoff = CounterRentalSchedule.CashHoldExpiresAtUtc(booking.PickupDate, booking.Policy);
        var deadline = method == "cash"
            ? cashCutoff
            : DateTime.UtcNow.AddMinutes(booking.Policy.BookingPaymentHoldMinutes);
        if (method == "qr" && rental.Method != PaymentMethods.Cash &&
            booking.ReservationExpiresAt.HasValue)
        {
            deadline = booking.ReservationExpiresAt.Value;
        }
        if (deadline > cashCutoff) deadline = cashCutoff;
        if (deadline <= DateTime.UtcNow)
        {
            TempData["ErrorMessage"] = "Đã quá thời hạn thanh toán tiền mặt tại quầy của lịch nhận này.";
            return RedirectToAction("Details", "Bookings", new { id = bookingId });
        }

        var selectedMethod = method == "cash" ? PaymentMethods.Cash : PaymentMethods.BankQr;
        rental.Method = selectedMethod;
        foreach (var deposit in booking.Payments.Where(item =>
                     item.Type == PaymentType.Deposit && item.Status == PaymentStatus.Pending))
        {
            deposit.Method = selectedMethod;
        }
        booking.ReservationExpiresAt = deadline;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TempData["SuccessMessage"] = method == "cash"
            ? $"Đã chọn tiền mặt. Hãy thanh toán trước {booking.PickupDate.AddMinutes(booking.Policy.NoShowGraceMinutes):dd/MM/yyyy HH:mm}; nhân viên chỉ xác nhận sau khi thực nhận đủ tiền thuê và cọc."
            : $"Đã chọn QR/chuyển khoản. Hãy báo đã chuyển trước {deadline.ToLocalTime():dd/MM/yyyy HH:mm}.";
        return RedirectToAction("Details", "Bookings", new { id = bookingId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitQr(
        int bookingId,
        PaymentType type,
        int? paymentId,
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
            cancellationToken,
            paymentId);

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
