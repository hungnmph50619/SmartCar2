using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Enums;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingWorkflowRulesTests
{
    [Theory]
    [InlineData(1000000, 3000000, 0, false)]
    [InlineData(1000000, 3000000, 2999999, false)]
    [InlineData(1000000, 3000000, 3000000, true)]
    [InlineData(1000000, 0, 0, true)]
    [InlineData(0, 0, 0, false)]
    public void HasRequiredUpfrontPayment_RequiresRentalAndFullDeposit(
        decimal rentalPaid,
        decimal requiredDeposit,
        decimal depositPaid,
        bool expected)
    {
        Assert.Equal(
            expected,
            BookingWorkflowRules.HasRequiredUpfrontPayment(
                rentalPaid,
                requiredDeposit,
                depositPaid));
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
        Assert.True(BookingWorkflowRules.CanCancelBeforeHandover(status, hasHandover: false));
    }

    [Theory]
    [InlineData(BookingStatus.PendingConfirmation)]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    public void CanCancelBeforeHandover_RejectsOnceHandoverRecordExists(BookingStatus status)
    {
        Assert.False(BookingWorkflowRules.CanCancelBeforeHandover(status, hasHandover: true));
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Paid)]
    [InlineData(BookingStatus.ReadyForPickup)]
    public void CanCancelBeforeHandover_RejectsWhileTransferAwaitsReconciliation(BookingStatus status)
    {
        Assert.False(BookingWorkflowRules.CanCancelBeforeHandover(
            status,
            hasHandover: false,
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
        Assert.False(BookingWorkflowRules.CanCancelBeforeHandover(status, hasHandover: false));
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
    public void CanPrepareHandover_AllowsDraftBeforePickupButNotAfterReturn()
    {
        var pickup = new DateTime(2026, 9, 20, 10, 0, 0);
        var returnAt = new DateTime(2026, 9, 21, 10, 0, 0);

        Assert.True(BookingWorkflowRules.CanPrepareHandover(
            pickup.AddHours(-2),
            returnAt));
        Assert.False(BookingWorkflowRules.CanPrepareHandover(
            returnAt,
            returnAt));
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
}
