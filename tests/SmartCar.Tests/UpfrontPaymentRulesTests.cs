using SmartCar.Application.Features.Payments;
using SmartCar.Domain.Enums;
using Xunit;

namespace SmartCar.Tests;

public sealed class UpfrontPaymentRulesTests
{
    [Fact]
    public void Assess_RequiresFullRentalAndDepositBeforeSettlement()
    {
        var assessment = UpfrontPaymentRules.Assess(
            rentalAmount: 1_000_000m,
            totalAmount: 1_200_000m,
            additionalAmount: 0m,
            depositAmount: 3_000_000m,
            new[]
            {
                new UpfrontPaymentEntry(PaymentType.Rental, 1_200_000m, PaymentStatus.Paid),
                new UpfrontPaymentEntry(PaymentType.Deposit, 2_500_000m, PaymentStatus.Paid)
            });

        Assert.Equal(1_200_000m, assessment.RequiredRentalAmount);
        Assert.Equal(0m, assessment.MissingRentalAmount);
        Assert.Equal(500_000m, assessment.MissingDepositAmount);
        Assert.False(assessment.IsSettled);
    }

    [Fact]
    public void Assess_DoesNotCountPendingOrAwaitingConfirmationAsPaid()
    {
        var assessment = UpfrontPaymentRules.Assess(
            rentalAmount: 900_000m,
            totalAmount: 900_000m,
            additionalAmount: 0m,
            depositAmount: 1_000_000m,
            new[]
            {
                new UpfrontPaymentEntry(PaymentType.Rental, 900_000m, PaymentStatus.AwaitingConfirmation),
                new UpfrontPaymentEntry(PaymentType.Deposit, 1_000_000m, PaymentStatus.Pending)
            });

        Assert.Equal(900_000m, assessment.MissingRentalAmount);
        Assert.Equal(1_000_000m, assessment.MissingDepositAmount);
        Assert.False(assessment.IsSettled);
    }

    [Fact]
    public void Assess_IgnoresUnrelatedPaymentTypes()
    {
        var assessment = UpfrontPaymentRules.Assess(
            rentalAmount: 800_000m,
            totalAmount: 950_000m,
            additionalAmount: 150_000m,
            depositAmount: 0m,
            new[]
            {
                new UpfrontPaymentEntry(PaymentType.AdditionalCharge, 950_000m, PaymentStatus.Paid),
                new UpfrontPaymentEntry(PaymentType.Refund, 5_000_000m, PaymentStatus.Paid)
            });

        Assert.Equal(800_000m, assessment.RequiredRentalAmount);
        Assert.Equal(800_000m, assessment.MissingRentalAmount);
        Assert.Equal(0m, assessment.MissingDepositAmount);
        Assert.False(assessment.IsSettled);
    }

    [Fact]
    public void Assess_ZeroDepositSettlesWhenRentalIsFullyPaid()
    {
        var assessment = UpfrontPaymentRules.Assess(
            rentalAmount: 700_000m,
            totalAmount: 850_000m,
            additionalAmount: 0m,
            depositAmount: 0m,
            new[]
            {
                new UpfrontPaymentEntry(PaymentType.Rental, 850_000m, PaymentStatus.Paid)
            });

        Assert.Equal(850_000m, assessment.RequiredRentalAmount);
        Assert.True(assessment.IsSettled);
    }

    [Fact]
    public void RequiredRentalAmount_NeverDropsBelowRentalAmountForLegacyTotals()
    {
        var required = UpfrontPaymentRules.GetRequiredRentalAmount(
            rentalAmount: 1_000_000m,
            totalAmount: 900_000m,
            additionalAmount: 0m);

        Assert.Equal(1_000_000m, required);
    }

    [Fact]
    public void RemainingDeposit_UsesAlreadyPaidDepositInsteadOfChargingFullDepositAgain()
    {
        var assessment = UpfrontPaymentRules.Assess(
            rentalAmount: 1_000_000m,
            totalAmount: 1_000_000m,
            additionalAmount: 0m,
            depositAmount: 3_000_000m,
            new[]
            {
                new UpfrontPaymentEntry(PaymentType.Deposit, 1_250_000m, PaymentStatus.Paid)
            });

        Assert.Equal(1_750_000m, assessment.MissingDepositAmount);
    }
}
