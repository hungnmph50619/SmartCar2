using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SmartCar.Application.Features.Bookings;
using SmartCar.Domain.Constants;

namespace SmartCar.Infrastructure.Services;

internal sealed class DeliveryQuoteService : IDeliveryQuoteService
{
    private static readonly SemaphoreSlim NominatimGate = new(1, 1);
    private static DateTimeOffset _lastNominatimRequest = DateTimeOffset.MinValue;

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;

    public DeliveryQuoteService(HttpClient httpClient, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _cache = cache;
    }

    public async Task<IReadOnlyList<DeliveryLocationSuggestion>> SearchLocationsAsync(
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var keyword = query?.Trim() ?? string.Empty;
        if (keyword.Length < 2)
        {
            return Array.Empty<DeliveryLocationSuggestion>();
        }

        limit = Math.Clamp(limit, 1, 8);
        var searchQuery = keyword.Contains("Hà Nội", StringComparison.OrdinalIgnoreCase) ||
                          keyword.Contains("Hanoi", StringComparison.OrdinalIgnoreCase)
            ? keyword
            : $"{keyword}, Hà Nội, Việt Nam";

        var cacheKey = $"delivery-search:{limit}:{searchQuery.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<DeliveryLocationSuggestion>? cached) && cached is not null)
        {
            return cached;
        }

        await NominatimGate.WaitAsync(cancellationToken);
        try
        {
            await WaitForNominatimSlotAsync(cancellationToken);

            var url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={Uri.EscapeDataString(searchQuery)}" +
                $"&format=jsonv2&limit={Math.Min(limit * 2, 10)}&countrycodes=vn&addressdetails=1";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            _lastNominatimRequest = DateTimeOffset.UtcNow;
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<DeliveryLocationSuggestion>();
            }

            var results = new List<DeliveryLocationSuggestion>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in json.RootElement.EnumerateArray())
            {
                var displayName = item.TryGetProperty("display_name", out var displayNameElement)
                    ? displayNameElement.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(displayName) ||
                    (!displayName.Contains("Hà Nội", StringComparison.OrdinalIgnoreCase) &&
                     !displayName.Contains("Hanoi", StringComparison.OrdinalIgnoreCase)) ||
                    !seenNames.Add(displayName))
                {
                    continue;
                }

                if (!item.TryGetProperty("lat", out var latElement) ||
                    !item.TryGetProperty("lon", out var lonElement) ||
                    !double.TryParse(latElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                    !double.TryParse(lonElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                {
                    continue;
                }

                results.Add(new DeliveryLocationSuggestion(displayName, latitude, longitude));
                if (results.Count >= limit)
                {
                    break;
                }
            }

            _cache.Set(cacheKey, results, TimeSpan.FromMinutes(30));
            return results;
        }
        finally
        {
            NominatimGate.Release();
        }
    }

    public async Task<DeliveryQuoteResult> CalculateAsync(
        string pickupMethod,
        string pickupLocation,
        string returnLocation,
        CancellationToken cancellationToken = default)
    {
        var normalizedMethod = pickupMethod?.Trim() ?? string.Empty;
        var pickup = pickupLocation?.Trim() ?? string.Empty;
        var returnPlace = returnLocation?.Trim() ?? string.Empty;

        if (normalizedMethod is not (DeliveryConstants.SelfPickup or DeliveryConstants.HomeDelivery))
        {
            return DeliveryQuoteResult.Failure("Hình thức nhận xe không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(returnPlace))
        {
            return DeliveryQuoteResult.Failure("Vui lòng nhập đầy đủ địa điểm nhận và trả xe.");
        }

        if (normalizedMethod == DeliveryConstants.SelfPickup)
        {
            pickup = DeliveryConstants.SmartCarLocation;
        }

        var pickupNeedsDelivery = normalizedMethod == DeliveryConstants.HomeDelivery;
        var returnNeedsCollection = !IsSmartCarLocation(returnPlace);

        if (!pickupNeedsDelivery && !returnNeedsCollection)
        {
            return new DeliveryQuoteResult(
                true,
                null,
                DeliveryConstants.SmartCarLocation,
                DeliveryConstants.SmartCarLocation,
                0,
                0,
                DeliveryConstants.RatePerKm,
                0);
        }

        GeoPoint? origin;
        try
        {
            origin = await GeocodeHanoiAsync(
                DeliveryConstants.SmartCarMapQuery,
                requireHanoi: true,
                cancellationToken);
        }
        catch
        {
            return DeliveryQuoteResult.Failure(
                "Chưa thể kết nối dịch vụ bản đồ để tính phí giao nhận. Vui lòng thử lại.");
        }

        if (origin is null)
        {
            return DeliveryQuoteResult.Failure("Không xác định được điểm giao nhận SmartCar trên bản đồ.");
        }

        decimal pickupDistance = 0;
        decimal returnDistance = 0;
        GeoPoint? pickupPoint = null;

        if (pickupNeedsDelivery)
        {
            try
            {
                pickupPoint = await GeocodeHanoiAsync(pickup, requireHanoi: true, cancellationToken);
            }
            catch
            {
                return DeliveryQuoteResult.Failure(
                    "Chưa thể xác định địa chỉ giao xe. Vui lòng kiểm tra kết nối và thử lại.");
            }

            if (pickupPoint is null)
            {
                return DeliveryQuoteResult.Failure(
                    "Không tìm thấy địa chỉ giao xe trong Hà Nội. Hãy chọn địa điểm từ danh sách gợi ý.");
            }

            pickupDistance = await GetRouteDistanceKmAsync(origin, pickupPoint, cancellationToken);
            if (pickupDistance <= 0)
            {
                return DeliveryQuoteResult.Failure("Không tìm được tuyến đường ô tô đến địa chỉ giao xe.");
            }

            if (pickupDistance > DeliveryConstants.MaxDistancePerLegKm)
            {
                return DeliveryQuoteResult.Failure(
                    $"Địa chỉ giao xe cách SmartCar {pickupDistance:N1} km, vượt phạm vi hỗ trợ tối đa {DeliveryConstants.MaxDistancePerLegKm:N0} km/lượt.");
            }
        }

        if (returnNeedsCollection)
        {
            GeoPoint? returnPoint;

            if (pickupPoint is not null && SameAddress(pickup, returnPlace))
            {
                returnPoint = pickupPoint;
            }
            else
            {
                try
                {
                    returnPoint = await GeocodeHanoiAsync(returnPlace, requireHanoi: true, cancellationToken);
                }
                catch
                {
                    return DeliveryQuoteResult.Failure(
                        "Chưa thể xác định địa chỉ trả xe. Vui lòng kiểm tra kết nối và thử lại.");
                }
            }

            if (returnPoint is null)
            {
                return DeliveryQuoteResult.Failure(
                    "Không tìm thấy địa chỉ trả xe trong Hà Nội. Hãy chọn địa điểm từ danh sách gợi ý.");
            }

            if (pickupPoint is not null && SameAddress(pickup, returnPlace))
            {
                returnDistance = pickupDistance;
            }
            else
            {
                returnDistance = await GetRouteDistanceKmAsync(origin, returnPoint, cancellationToken);
            }

            if (returnDistance <= 0)
            {
                return DeliveryQuoteResult.Failure("Không tìm được tuyến đường ô tô đến địa điểm trả xe.");
            }

            if (returnDistance > DeliveryConstants.MaxDistancePerLegKm)
            {
                return DeliveryQuoteResult.Failure(
                    $"Địa điểm trả xe cách SmartCar {returnDistance:N1} km, vượt phạm vi hỗ trợ tối đa {DeliveryConstants.MaxDistancePerLegKm:N0} km/lượt.");
            }
        }

        var totalDistance = pickupDistance + returnDistance;
        var fee = Math.Round(
            totalDistance * DeliveryConstants.RatePerKm,
            0,
            MidpointRounding.AwayFromZero);

        return new DeliveryQuoteResult(
            true,
            null,
            pickup,
            returnPlace,
            pickupDistance,
            returnDistance,
            DeliveryConstants.RatePerKm,
            fee);
    }

    private async Task<GeoPoint?> GeocodeHanoiAsync(
        string address,
        bool requireHanoi,
        CancellationToken cancellationToken)
    {
        var query = address.Contains("Hà Nội", StringComparison.OrdinalIgnoreCase) ||
                    address.Contains("Hanoi", StringComparison.OrdinalIgnoreCase)
            ? address
            : $"{address}, Hà Nội, Việt Nam";

        var cacheKey = $"delivery-geocode:{query.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out GeoPoint cachedPoint))
        {
            return cachedPoint;
        }

        await NominatimGate.WaitAsync(cancellationToken);
        try
        {
            await WaitForNominatimSlotAsync(cancellationToken);

            var url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={Uri.EscapeDataString(query)}" +
                "&format=jsonv2&limit=1&countrycodes=vn&addressdetails=1";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            _lastNominatimRequest = DateTimeOffset.UtcNow;
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = json.RootElement[0];
            var displayName = first.GetProperty("display_name").GetString() ?? string.Empty;

            if (requireHanoi &&
                !displayName.Contains("Hà Nội", StringComparison.OrdinalIgnoreCase) &&
                !displayName.Contains("Hanoi", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!double.TryParse(first.GetProperty("lat").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(first.GetProperty("lon").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                return null;
            }

            var point = new GeoPoint(lat, lon, displayName);
            _cache.Set(cacheKey, point, TimeSpan.FromHours(12));
            return point;
        }
        finally
        {
            NominatimGate.Release();
        }
    }

    private async Task<decimal> GetRouteDistanceKmAsync(
        GeoPoint origin,
        GeoPoint destination,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"delivery-route:{origin.Latitude:F5},{origin.Longitude:F5}:{destination.Latitude:F5},{destination.Longitude:F5}");

        if (_cache.TryGetValue(cacheKey, out decimal cachedDistance))
        {
            return cachedDistance;
        }

        var coordinates = string.Create(
            CultureInfo.InvariantCulture,
            $"{origin.Longitude:F6},{origin.Latitude:F6};{destination.Longitude:F6},{destination.Latitude:F6}");

        var url = $"https://router.project-osrm.org/route/v1/driving/{coordinates}?overview=false";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("code", out var code) || code.GetString() != "Ok" ||
            !json.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
        {
            return 0;
        }

        var meters = routes[0].GetProperty("distance").GetDouble();
        var distanceKm = Math.Round((decimal)(meters / 1000d), 1, MidpointRounding.AwayFromZero);
        _cache.Set(cacheKey, distanceKm, TimeSpan.FromHours(6));
        return distanceKm;
    }

    private static async Task WaitForNominatimSlotAsync(CancellationToken cancellationToken)
    {
        var elapsed = DateTimeOffset.UtcNow - _lastNominatimRequest;
        var minimumDelay = TimeSpan.FromMilliseconds(1100);
        if (elapsed < minimumDelay)
        {
            await Task.Delay(minimumDelay - elapsed, cancellationToken);
        }
    }

    private static bool IsSmartCarLocation(string location) =>
        SameAddress(location, DeliveryConstants.SmartCarLocation) ||
        SameAddress(location, DeliveryConstants.SmartCarMapQuery);

    private static bool SameAddress(string left, string right) =>
        string.Equals(
            left.Trim().Replace("  ", " "),
            right.Trim().Replace("  ", " "),
            StringComparison.OrdinalIgnoreCase);

    private sealed record GeoPoint(double Latitude, double Longitude, string DisplayName);
}
