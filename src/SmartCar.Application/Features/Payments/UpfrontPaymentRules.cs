using SmartCar.Domain.Enums;

namespace SmartCar.Application.Features.Payments;

public readonly record struct UpfrontPaymentEntry(
    PaymentType Type,
    decimal Amount,
    PaymentStatus Status);

public sealed record UpfrontPaymentAssessment(
    decimal RequiredRentalAmount,
    decimal PaidRentalAmount,
    decimal RequiredDepositAmount,
    decimal PaidDepositAmount)
{
    public decimal MissingRentalAmount => Math.Max(0m, RequiredRentalAmount - PaidRentalAmount);
    public decimal MissingDepositAmount => Math.Max(0m, RequiredDepositAmount - PaidDepositAmount);
    public bool IsSettled => MissingRentalAmount <= 0m && MissingDepositAmount <= 0m;
}

public static class UpfrontPaymentRules
{
    public static decimal GetRequiredRentalAmount(
        decimal rentalAmount,
        decimal totalAmount,
        decimal additionalAmount)
    {
        var normalizedRental = Math.Max(0m, rentalAmount);
        var storedDeliveryFee = Math.Max(
            0m,
            totalAmount - normalizedRental - Math.Max(0m, additionalAmount));

        return normalizedRental + storedDeliveryFee;
    }

    public static UpfrontPaymentAssessment Assess(
        decimal rentalAmount,
        decimal totalAmount,
        decimal additionalAmount,
        decimal depositAmount,
        IEnumerable<UpfrontPaymentEntry> payments)
    {
        var entries = payments ?? Array.Empty<UpfrontPaymentEntry>();
        var paidRental = entries
            .Where(payment =>
                payment.Type == PaymentType.Rental &&
                payment.Status == PaymentStatus.Paid &&
                payment.Amount > 0m)
            .Sum(payment => payment.Amount);
        var paidDeposit = entries
            .Where(payment =>
                payment.Type == PaymentType.Deposit &&
                payment.Status == PaymentStatus.Paid &&
                payment.Amount > 0m)
            .Sum(payment => payment.Amount);

        return new UpfrontPaymentAssessment(
            GetRequiredRentalAmount(rentalAmount, totalAmount, additionalAmount),
            paidRental,
            Math.Max(0m, depositAmount),
            paidDeposit);
    }
}
