namespace SmartCar.Application.Features.Bookings;

public sealed record DeliveryQuoteResult(
    bool Succeeded,
    string? Error,
    string PickupLocation,
    string ReturnLocation,
    decimal PickupDeliveryDistanceKm,
    decimal ReturnCollectionDistanceKm,
    decimal DeliveryRatePerKm,
    decimal DeliveryFee)
{
    public decimal TotalDistanceKm => PickupDeliveryDistanceKm + ReturnCollectionDistanceKm;

    public static DeliveryQuoteResult Failure(string error) =>
        new(false, error, string.Empty, string.Empty, 0, 0, 0, 0);
}

public sealed record DeliveryLocationSuggestion(
    string DisplayName,
    double Latitude,
    double Longitude);

public interface IDeliveryQuoteService
{
    Task<DeliveryQuoteResult> CalculateAsync(
        string pickupMethod,
        string pickupLocation,
        string returnLocation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliveryLocationSuggestion>> SearchLocationsAsync(
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default);
}
