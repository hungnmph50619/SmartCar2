using System.Globalization;

namespace SmartCar.Domain.Constants;

public static class CompensationLedger
{
    public const string Marker = "[NEXT_BOOKING_COMPENSATION]";

    public static decimal SumReservedAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        var total = 0m;
        var searchIndex = 0;

        while (searchIndex < value.Length)
        {
            var markerIndex = value.IndexOf(Marker, searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                break;
            }

            var valueStart = markerIndex + Marker.Length;
            var valueEnd = value.IndexOfAny(new[] { '|', '\r', '\n' }, valueStart);
            var rawValue = valueEnd < 0
                ? value[valueStart..]
                : value[valueStart..valueEnd];

            if ((decimal.TryParse(
                    rawValue.Trim(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var invariantAmount) &&
                 invariantAmount > 0) ||
                (decimal.TryParse(
                    rawValue.Trim(),
                    NumberStyles.Number,
                    CultureInfo.CurrentCulture,
                    out invariantAmount) &&
                 invariantAmount > 0))
            {
                total += invariantAmount;
            }

            searchIndex = valueEnd < 0 ? value.Length : valueEnd + 1;
        }

        return total;
    }
}
