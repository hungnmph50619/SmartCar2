using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Operations;

public static class BookingWorkflowRules
{
    private static readonly BookingStatus[] CancellableBeforeHandoverStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup
    };

    private static readonly BookingStatus[] ActiveStaffStatuses =
    {
        BookingStatus.PendingConfirmation,
        BookingStatus.PendingPayment,
        BookingStatus.Paid,
        BookingStatus.ReadyForPickup,
        BookingStatus.Rented,
        BookingStatus.PendingInspection,
        BookingStatus.AwaitingRefund
    };

    public static bool CanCancelBeforeHandover(
        BookingStatus status,
        bool hasHandover,
        bool hasPaymentAwaitingConfirmation = false) =>
        !hasHandover &&
        !hasPaymentAwaitingConfirmation &&
        CancellableBeforeHandoverStatuses.Contains(status);

    public static bool HasRequiredUpfrontPayment(
        decimal requiredRental,
        decimal rentalPaid,
        decimal requiredDeposit,
        decimal depositPaid) =>
        CalculateOutstandingRental(requiredRental, rentalPaid) == 0m &&
        CalculateOutstandingDeposit(requiredDeposit, depositPaid) == 0m;

    public static decimal CalculateOutstandingRental(
        decimal requiredRental,
        decimal rentalPaid) =>
        Math.Max(0m, Math.Max(0m, requiredRental) - Math.Max(0m, rentalPaid));

    public static decimal CalculateOutstandingDeposit(
        decimal requiredDeposit,
        decimal depositPaid) =>
        Math.Max(0m, Math.Max(0m, requiredDeposit) - Math.Max(0m, depositPaid));

    public static decimal CalculateEffectivePaid(
        decimal grossPaid,
        decimal refundedOrPlanned) =>
        Math.Max(0m, Math.Max(0m, grossPaid) - Math.Max(0m, refundedOrPlanned));

    public static bool IsOpenRefundStatus(PaymentStatus status) =>
        status is PaymentStatus.AwaitingRefund or PaymentStatus.RefundApproved;

    public static bool CountsTowardRefundTotal(PaymentStatus status) =>
        status is PaymentStatus.AwaitingRefund
            or PaymentStatus.RefundApproved
            or PaymentStatus.Refunded;

    public static bool ShouldVoidPendingCollectionOnTerminalBooking(
        PaymentType type,
        PaymentStatus status) =>
        type != PaymentType.Refund &&
        status == PaymentStatus.Pending;

    public static bool IsReturnSurchargePaymentType(PaymentType type) =>
        type == PaymentType.AdditionalCharge;

    public static bool IsOutstandingTrafficFine(
        PaymentType type,
        PaymentStatus status,
        decimal amount) =>
        type == PaymentType.TrafficFine &&
        amount > 0m &&
        status is PaymentStatus.Pending or
            PaymentStatus.AwaitingConfirmation or
            PaymentStatus.Failed;

    public static bool PreserveForLateReconciliationOnReservationExpiry(
        PaymentStatus status) =>
        status == PaymentStatus.AwaitingConfirmation;

    public static bool CanReconcileTransferAfterReservationExpiry(
        BookingStatus bookingStatus,
        PaymentType paymentType) =>
        bookingStatus == BookingStatus.Expired &&
        paymentType is PaymentType.Rental or PaymentType.VehicleSwapAdjustment;

    public static bool ShouldCancelExtensionWhenVehicleReturns(
        BookingExtensionStatus status) =>
        status is BookingExtensionStatus.Pending
            or BookingExtensionStatus.NeedsEvidence
            or BookingExtensionStatus.Approved;

    public static bool BlocksVehicleReturnForExtensionPayment(
        PaymentStatus status) =>
        status == PaymentStatus.AwaitingConfirmation;

    public static bool IsExtensionPaymentLedgerConsistent(
        decimal paidExtensionAmount,
        decimal effectivePaidExtensionAmount) =>
        Math.Max(0m, paidExtensionAmount) ==
        Math.Max(0m, effectivePaidExtensionAmount);

    public static bool CanStaffReview(
        BookingStatus status,
        DateTime? staffReviewedAt) =>
        status == BookingStatus.PendingConfirmation && !staffReviewedAt.HasValue;

    // Biên bản nháp có thể chuẩn bị trước giờ nhận để Staff không phải đợi đến đúng phút
    // mới bắt đầu nhập ảnh/tình trạng. Chuyến chỉ được bắt đầu bởi CanStartTrip.
    public static bool CanPrepareHandover(
        DateTime now,
        DateTime returnDate) =>
        now < returnDate;

    public static bool CanStartTrip(
        DateTime now,
        DateTime pickupDate,
        DateTime returnDate) =>
        now >= pickupDate && now < returnDate;

    public static bool CanRecordReturn(
        DateTime returnedAt,
        DateTime handoverAt,
        DateTime now,
        int futureGraceMinutes = 5) =>
        returnedAt >= handoverAt &&
        returnedAt <= now.AddMinutes(Math.Max(0, futureGraceMinutes));

    public static bool IsStaffWorkItem(
        BookingStatus status,
        bool hasOpenRefund)
    {
        if (ActiveStaffStatuses.Contains(status))
        {
            return true;
        }

        return hasOpenRefund &&
               status is BookingStatus.Cancelled or BookingStatus.NoShow;
    }
}
