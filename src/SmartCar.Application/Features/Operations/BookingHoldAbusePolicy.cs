namespace SmartCar.Application.Features.Operations;

public sealed record BookingHoldRestriction(
    int TimeoutCount24Hours,
    DateTime? SameVehicleCooldownUntil,
    DateTime? AccountBlockedUntil)
{
    public bool IsAccountBlocked(DateTime utcNow) =>
        AccountBlockedUntil.HasValue && AccountBlockedUntil.Value > utcNow;

    public bool IsVehicleCoolingDown(DateTime utcNow) =>
        SameVehicleCooldownUntil.HasValue && SameVehicleCooldownUntil.Value > utcNow;
}

public static class BookingHoldAbusePolicy
{
    public const int RollingWindowHours = 24;
    public const int FirstVehicleCooldownMinutes = 30;
    public const int RepeatedVehicleCooldownMinutes = 120;
    public const int AccountBlockAfterTimeouts = 3;
    public const int AccountBlockHours = 24;

    public static BookingHoldRestriction Evaluate(
        DateTime utcNow,
        IReadOnlyCollection<DateTime> allCustomerTimeouts,
        IReadOnlyCollection<DateTime> sameVehicleTimeouts)
    {
        var windowStart = utcNow.AddHours(-RollingWindowHours);
        var recentAll = allCustomerTimeouts
            .Where(item => item >= windowStart && item <= utcNow)
            .OrderBy(item => item)
            .ToArray();
        var recentVehicle = sameVehicleTimeouts
            .Where(item => item >= windowStart && item <= utcNow)
            .OrderBy(item => item)
            .ToArray();

        DateTime? vehicleCooldownUntil = null;
        if (recentVehicle.Length == 1)
        {
            vehicleCooldownUntil = recentVehicle[^1].AddMinutes(FirstVehicleCooldownMinutes);
        }
        else if (recentVehicle.Length >= 2)
        {
            vehicleCooldownUntil = recentVehicle[^1].AddMinutes(RepeatedVehicleCooldownMinutes);
        }

        DateTime? accountBlockedUntil = null;
        if (recentAll.Length >= AccountBlockAfterTimeouts)
        {
            accountBlockedUntil = recentAll[^1].AddHours(AccountBlockHours);
        }

        return new BookingHoldRestriction(
            recentAll.Length,
            vehicleCooldownUntil,
            accountBlockedUntil);
    }
}
