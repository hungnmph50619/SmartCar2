using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingWorkflowRulesTests
{
    [Theory]
    [InlineData(1000000, 0, 3000000, 3000000, false)]
    [InlineData(1000000, 999999, 3000000, 3000000, false)]
    [InlineData(1000000, 1000000, 3000000, 2999999, false)]
    [InlineData(1000000, 1000000, 3000000, 3000000, true)]
    [InlineData(1000000, 1200000, 3000000, 3500000, true)]
    [InlineData(1000000, 1000000, 0, 0, true)]
    public void HasRequiredUpfrontPayment_RequiresFullRentalAndFullDeposit(
        decimal requiredRental,
        decimal rentalPaid,
        decimal requiredDeposit,
        decimal depositPaid,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.HasRequiredUpfrontPayment(
                requiredRental,
                rentalPaid,
                requiredDeposit,
                depositPaid));
    }

    [Theory]
    [InlineData(1000000, 0, 1000000)]
    [InlineData(1000000, 400000, 600000)]
    [InlineData(1000000, 1000000, 0)]
    [InlineData(1000000, 1200000, 0)]
    public void CalculateOutstandingRental_NeverCreatesOverpayment(
        decimal requiredRental,
        decimal rentalPaid,
        decimal expectedOutstanding)
    {
        Assert.Equal(
            expectedOutstanding,
            BookingWorkflowRules.CalculateOutstandingRental(
                requiredRental,
                rentalPaid));
    }

    [Theory]
    [InlineData(3000000, 0, 3000000)]
    [InlineData(3000000, 1000000, 2000000)]
    [InlineData(3000000, 3000000, 0)]
    [InlineData(3000000, 4000000, 0)]
    [InlineData(0, 0, 0)]
    public void CalculateOutstandingDeposit_NeverCreatesOverpayment(
        decimal requiredDeposit,
        decimal depositPaid,
        decimal expectedOutstanding)
    {
        Assert.Equal(
            expectedOutstanding,
            BookingWorkflowRules.CalculateOutstandingDeposit(requiredDeposit, depositPaid));
    }

    [Theory]
    [InlineData(BookingStatus.PendingConfirmation)]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    public void CanCancelBeforeHandover_AllowsPreHandoverStatuses(BookingStatus status)
    {
        Assert.True(BookingWorkflowRules.CanCancelBeforeHandover(status, handoverStarted: false));
    }

    [Theory]
    [InlineData(BookingStatus.PendingConfirmation)]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    public void CanCancelBeforeHandover_RejectsOnceTripActuallyStarted(BookingStatus status)
    {
        Assert.False(BookingWorkflowRules.CanCancelBeforeHandover(status, handoverStarted: true));
    }

    [Theory]
    [InlineData(BookingStatus.PendingConfirmation)]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    public void CanCancelBeforeHandover_AllowsUnsignedDraftHandover(BookingStatus status)
    {
        Assert.True(BookingWorkflowRules.CanCancelBeforeHandover(status, handoverStarted: false));
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    public void CanCancelBeforeHandover_RejectsWhileTransferAwaitsReconciliation(BookingStatus status)
    {
        Assert.False(BookingWorkflowRules.CanCancelBeforeHandover(
            status,
            handoverStarted: false,
            hasPaymentAwaitingConfirmation: true));
    }

    [Theory]
    [InlineData(BookingStatus.Rented)]
    [InlineData(BookingStatus.PendingInspection)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.NoShow)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Expired)]
    public void CanCancelBeforeHandover_RejectsTerminalOrStartedStatuses(BookingStatus status)
    {
        Assert.False(BookingWorkflowRules.CanCancelBeforeHandover(status, handoverStarted: false));
    }

    [Theory]
    [InlineData(BookingStatus.PendingConfirmation)]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    [InlineData(BookingStatus.Rented)]
    [InlineData(BookingStatus.PendingInspection)]
    [InlineData(BookingStatus.AwaitingRefund)]
    public void IsStaffWorkItem_IncludesActiveOperationalStatuses(BookingStatus status)
    {
        Assert.True(BookingWorkflowRules.IsStaffWorkItem(status, hasOpenRefund: false));
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.NoShow)]
    public void IsStaffWorkItem_IncludesTerminalBookingWhenRefundIsStillOpen(BookingStatus status)
    {
        Assert.True(BookingWorkflowRules.IsStaffWorkItem(status, hasOpenRefund: true));
        Assert.False(BookingWorkflowRules.IsStaffWorkItem(status, hasOpenRefund: false));
    }

    [Theory]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Expired)]
    public void IsStaffWorkItem_DoesNotReopenUnrelatedTerminalBookings(BookingStatus status)
    {
        Assert.False(BookingWorkflowRules.IsStaffWorkItem(status, hasOpenRefund: false));
    }

    [Fact]
    public void CanStaffReview_AllowsPendingConfirmationOnlyBeforeReview()
    {
        Assert.True(BookingWorkflowRules.CanStaffReview(
            BookingStatus.PendingConfirmation,
            staffReviewedAt: null));

        Assert.False(BookingWorkflowRules.CanStaffReview(
            BookingStatus.PendingConfirmation,
            staffReviewedAt: DateTime.UtcNow));
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    [InlineData(BookingStatus.Rented)]
    [InlineData(BookingStatus.Cancelled)]
    public void CanStaffReview_RejectsStatusesOutsidePendingConfirmation(BookingStatus status)
    {
        Assert.False(BookingWorkflowRules.CanStaffReview(status, staffReviewedAt: null));
    }

    [Fact]
    public void CanPrepareHandover_RequiresBookedPickupWindow()
    {
        var pickup = new DateTime(2026, 9, 20, 10, 0, 0);
        var returnAt = new DateTime(2026, 9, 21, 10, 0, 0);

        Assert.False(BookingWorkflowRules.CanPrepareHandover(pickup.AddSeconds(-1), pickup, returnAt));
        Assert.True(BookingWorkflowRules.CanPrepareHandover(pickup, pickup, returnAt));
        Assert.False(BookingWorkflowRules.CanPrepareHandover(returnAt, pickup, returnAt));
    }

    [Fact]
    public void HasActualTurnaroundElapsed_UsesNextBookingSnapshotForStorePickup()
    {
        var returnedAt = new DateTime(2026, 9, 25, 10, 0, 0);
        var policy = new RentalPolicySnapshot
        {
            VehicleTurnaroundMinutes = 75,
            DeliveryLeadMinutes = 20
        };

        Assert.False(BookingWorkflowRules.HasActualTurnaroundElapsed(
            returnedAt.AddMinutes(74),
            returnedAt,
            policy,
            VehiclePickupMethod.StorePickup));
        Assert.True(BookingWorkflowRules.HasActualTurnaroundElapsed(
            returnedAt.AddMinutes(75),
            returnedAt,
            policy,
            VehiclePickupMethod.StorePickup));
    }

    [Fact]
    public void HasActualTurnaroundElapsed_AddsDeliveryLeadForDeliveryPickup()
    {
        var returnedAt = new DateTime(2026, 9, 25, 10, 0, 0);
        var policy = new RentalPolicySnapshot
        {
            VehicleTurnaroundMinutes = 60,
            DeliveryLeadMinutes = 30
        };

        Assert.False(BookingWorkflowRules.HasActualTurnaroundElapsed(
            returnedAt.AddMinutes(89),
            returnedAt,
            policy,
            VehiclePickupMethod.Delivery));
        Assert.True(BookingWorkflowRules.HasActualTurnaroundElapsed(
            returnedAt.AddMinutes(90),
            returnedAt,
            policy,
            VehiclePickupMethod.Delivery));
    }

    [Fact]
    public void CanStartTrip_RequiresCurrentTimeInsideBookedWindow()
    {
        var pickup = new DateTime(2026, 9, 20, 10, 0, 0);
        var returnAt = new DateTime(2026, 9, 21, 10, 0, 0);

        Assert.False(BookingWorkflowRules.CanStartTrip(pickup.AddSeconds(-1), pickup, returnAt));
        Assert.True(BookingWorkflowRules.CanStartTrip(pickup, pickup, returnAt));
        Assert.True(BookingWorkflowRules.CanStartTrip(returnAt.AddSeconds(-1), pickup, returnAt));
        Assert.False(BookingWorkflowRules.CanStartTrip(returnAt, pickup, returnAt));
    }

    [Fact]
    public void CanRecordReturn_RejectsBeforeHandoverAndFutureBeyondGrace()
    {
        var handoverAt = new DateTime(2026, 9, 20, 10, 0, 0);
        var now = new DateTime(2026, 9, 20, 12, 0, 0);

        Assert.False(BookingWorkflowRules.CanRecordReturn(handoverAt.AddMinutes(-1), handoverAt, now));
        Assert.True(BookingWorkflowRules.CanRecordReturn(now, handoverAt, now));
        Assert.True(BookingWorkflowRules.CanRecordReturn(now.AddMinutes(5), handoverAt, now));
        Assert.False(BookingWorkflowRules.CanRecordReturn(now.AddMinutes(6), handoverAt, now));
    }

    [Theory]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.Pending, 500000, true)]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.AwaitingConfirmation, 500000, true)]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.Failed, 500000, true)]
    [InlineData(PaymentType.OverdueCompensationDebt, PaymentStatus.Pending, 500000, true)]
    [InlineData(PaymentType.OverdueCompensationDebt, PaymentStatus.AwaitingConfirmation, 500000, true)]
    [InlineData(PaymentType.OverdueCompensationDebt, PaymentStatus.Paid, 500000, false)]
    [InlineData(PaymentType.OverdueCompensationDebt, PaymentStatus.Pending, 0, false)]
    [InlineData(PaymentType.AdditionalCharge, PaymentStatus.Pending, 500000, false)]
    public void BlocksNewRentalForOutstandingCustomerObligation_UsesOnlyOpenLiabilities(
        PaymentType type,
        PaymentStatus status,
        decimal amount,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.BlocksNewRentalForOutstandingCustomerObligation(
                type,
                status,
                amount));
    }

    [Theory]
    [InlineData(PaymentStatus.Pending, 500000, true)]
    [InlineData(PaymentStatus.AwaitingConfirmation, 500000, true)]
    [InlineData(PaymentStatus.Failed, 500000, true)]
    [InlineData(PaymentStatus.Paid, 500000, false)]
    [InlineData(PaymentStatus.Pending, 0, false)]
    public void IsOutstandingTrafficFine_UsesOnlyOpenTrafficFineStates(
        PaymentStatus status,
        decimal amount,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.IsOutstandingTrafficFine(
                PaymentType.TrafficFine,
                status,
                amount));
        Assert.False(
            BookingWorkflowRules.IsOutstandingTrafficFine(
                PaymentType.AdditionalCharge,
                status,
                amount));
    }

    [Theory]
    [InlineData(PaymentStatus.AwaitingRefund, true)]
    [InlineData(PaymentStatus.RefundApproved, true)]
    [InlineData(PaymentStatus.Refunded, false)]
    [InlineData(PaymentStatus.Failed, false)]
    public void IsOpenRefundStatus_TracksApprovalUntilStaffTransfer(
        PaymentStatus status,
        bool expected)
    {
        Assert.Equal(expected, BookingWorkflowRules.IsOpenRefundStatus(status));
    }



    [Theory]
    [InlineData(BookingExtensionStatus.Pending, true)]
    [InlineData(BookingExtensionStatus.NeedsEvidence, true)]
    [InlineData(BookingExtensionStatus.Approved, true)]
    [InlineData(BookingExtensionStatus.Paid, false)]
    [InlineData(BookingExtensionStatus.Rejected, false)]
    [InlineData(BookingExtensionStatus.Cancelled, false)]
    public void ShouldCancelExtensionWhenVehicleReturns_OnlyCancelsUnsettledRequests(
        BookingExtensionStatus status,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.ShouldCancelExtensionWhenVehicleReturns(status));
    }

    [Theory]
    [InlineData(PaymentStatus.AwaitingConfirmation, true)]
    [InlineData(PaymentStatus.Pending, false)]
    [InlineData(PaymentStatus.Paid, false)]
    [InlineData(PaymentStatus.Failed, false)]
    public void BlocksVehicleReturnForExtensionPayment_OnlyBlocksBankReconciliation(
        PaymentStatus status,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.BlocksVehicleReturnForExtensionPayment(status));
    }


    [Theory]
    [InlineData(1000000, 0, 1000000)]
    [InlineData(1000000, 200000, 800000)]
    [InlineData(1000000, 1000000, 0)]
    [InlineData(1000000, 1500000, 0)]
    [InlineData(-1000, 0, 0)]
    public void CalculateEffectivePaid_SubtractsRefundsWithoutGoingNegative(
        decimal grossPaid,
        decimal refundedOrPlanned,
        decimal expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.CalculateEffectivePaid(
                grossPaid,
                refundedOrPlanned));
    }


    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(500000, 500000, true)]
    [InlineData(500000, 0, false)]
    [InlineData(0, 500000, false)]
    public void IsExtensionPaymentLedgerConsistent_RequiresPaidMoneyAndEffectiveExtensionToMatch(
        decimal paidExtensionAmount,
        decimal effectivePaidExtensionAmount,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.IsExtensionPaymentLedgerConsistent(
                paidExtensionAmount,
                effectivePaidExtensionAmount));
    }


    [Theory]
    [InlineData(PaymentStatus.AwaitingRefund, true)]
    [InlineData(PaymentStatus.RefundApproved, true)]
    [InlineData(PaymentStatus.Refunded, true)]
    [InlineData(PaymentStatus.Failed, false)]
    [InlineData(PaymentStatus.Pending, false)]
    [InlineData(PaymentStatus.AwaitingConfirmation, false)]
    public void CountsTowardRefundTotal_ExcludesFailedOrUnrelatedPayments(
        PaymentStatus status,
        bool expected)
    {
        Assert.Equal(expected, BookingWorkflowRules.CountsTowardRefundTotal(status));
    }


    [Theory]
    [InlineData(PaymentType.AdditionalCharge, true)]
    [InlineData(PaymentType.OverdueCompensationDebt, false)]
    [InlineData(PaymentType.TrafficFine, false)]
    [InlineData(PaymentType.Extension, false)]
    public void IsReturnSurchargePaymentType_SeparatesReturnChargesFromOtherDebts(
        PaymentType type,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.IsReturnSurchargePaymentType(type));
    }


    [Theory]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.Pending, 500000, true)]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.AwaitingConfirmation, 500000, true)]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.Failed, 500000, true)]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.Failed, 0, false)]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.Paid, 500000, false)]
    [InlineData(PaymentType.Rental, PaymentStatus.Pending, 500000, false)]
    public void IsOutstandingTrafficFine_OnlyBlocksPositiveOpenFineLedger(
        PaymentType type,
        PaymentStatus status,
        decimal amount,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.IsOutstandingTrafficFine(
                type,
                status,
                amount));
    }


    [Theory]
    [InlineData(PaymentType.Rental, PaymentStatus.Pending, true)]
    [InlineData(PaymentType.Deposit, PaymentStatus.Pending, true)]
    [InlineData(PaymentType.VehicleSwapAdjustment, PaymentStatus.Pending, true)]
    [InlineData(PaymentType.Extension, PaymentStatus.Pending, true)]
    [InlineData(PaymentType.AdditionalCharge, PaymentStatus.Pending, true)]
    [InlineData(PaymentType.TrafficFine, PaymentStatus.Pending, true)]
    [InlineData(PaymentType.Refund, PaymentStatus.Pending, false)]
    [InlineData(PaymentType.Rental, PaymentStatus.AwaitingConfirmation, false)]
    [InlineData(PaymentType.Rental, PaymentStatus.Paid, false)]
    [InlineData(PaymentType.Rental, PaymentStatus.Failed, false)]
    public void ShouldVoidPendingCollectionOnTerminalBooking_OnlyClosesUncollectedObligations(
        PaymentType type,
        PaymentStatus status,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.ShouldVoidPendingCollectionOnTerminalBooking(
                type,
                status));
    }


    [Theory]
    [InlineData(PaymentStatus.AwaitingConfirmation, true)]
    [InlineData(PaymentStatus.Pending, false)]
    [InlineData(PaymentStatus.Paid, false)]
    [InlineData(PaymentStatus.Failed, false)]
    public void PreserveForLateReconciliationOnReservationExpiry_OnlyKeepsMoneyInFlight(
        PaymentStatus status,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.PreserveForLateReconciliationOnReservationExpiry(status));
    }

    [Theory]
    [InlineData(BookingStatus.Expired, PaymentType.Rental, true)]
    [InlineData(BookingStatus.Expired, PaymentType.VehicleSwapAdjustment, true)]
    [InlineData(BookingStatus.Expired, PaymentType.Deposit, false)]
    [InlineData(BookingStatus.PendingPayment, PaymentType.Rental, false)]
    [InlineData(BookingStatus.Cancelled, PaymentType.Rental, false)]
    public void CanReconcileTransferAfterReservationExpiry_OnlyAllowsExpiredPreHandoverMoneyInFlight(
        BookingStatus bookingStatus,
        PaymentType paymentType,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.CanReconcileTransferAfterReservationExpiry(
                bookingStatus,
                paymentType));
    }
}

