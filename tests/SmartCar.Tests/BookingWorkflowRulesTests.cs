using SmartCar.Application.Features.Operations;
using SmartCar.Domain.Enums;
using Xunit;

namespace SmartCar.Tests;

public sealed class BookingWorkflowRulesTests
{
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
}
