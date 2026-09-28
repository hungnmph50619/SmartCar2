using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SmartCar.Web.Controllers;

[Route("api/geocoding")]
public sealed class GeocodingController : ControllerBase
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(6);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeocodingController> _logger;

    public GeocodingController(
        IHttpClientFactory httpClientFactory,
        ILogger<GeocodingController>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<GeocodingController>.Instance;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] double? nearLat,
        [FromQuery] double? nearLon,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 3 || query.Length > 200)
            return BadRequest(new { message = "Nhập địa chỉ từ 3 đến 200 ký tự." });

        var client = CreateProviderClient();
        var nominatimUrl = BuildNominatimSearchUrl(query, nearLat, nearLon);
        try
        {
            using var response = await client.GetAsync(nominatimUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (HasNominatimSearchResults(json))
                    return Content(json, "application/json");
            }
            else
            {
                _logger.LogWarning(
                    "Nominatim address search returned HTTP {StatusCode}; trying Photon.",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Nominatim address search timed out; trying Photon.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Nominatim address search is unreachable; trying Photon.");
        }

        var arcGisJson = await TryGetArcGisJsonAsync(
            client,
            BuildArcGisSearchUrl(query, nearLat, nearLon),
            reverse: false,
            cancellationToken);
        if (arcGisJson is not null)
            return Content(arcGisJson, "application/json");

        var photonJson = await TryGetPhotonJsonAsync(
            client,
            BuildPhotonSearchUrl(query, nearLat, nearLon),
            reverse: false,
            cancellationToken);
        if (photonJson is not null)
            return Content(photonJson, "application/json");

        return StatusCode(502, new
        {
            message = "Dịch vụ tìm địa chỉ tạm thời không phản hồi. Vui lòng thử lại sau."
        });
    }

    [HttpGet("reverse")]
    public async Task<IActionResult> Reverse(
        [FromQuery] double lat,
        [FromQuery] double lon,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lon) ||
            lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            return BadRequest(new { message = "Tọa độ không hợp lệ." });
        }

        var client = CreateProviderClient();
        var latText = lat.ToString("0.#######", CultureInfo.InvariantCulture);
        var lonText = lon.ToString("0.#######", CultureInfo.InvariantCulture);
        var nominatimUrl =
            $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi&lat={latText}&lon={lonText}";

        try
        {
            using var response = await client.GetAsync(nominatimUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (HasNominatimAddress(json))
                    return Content(json, "application/json");
            }
            else
            {
                _logger.LogWarning(
                    "Nominatim reverse geocoding returned HTTP {StatusCode}; trying Photon.",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Nominatim reverse geocoding timed out; trying Photon.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Nominatim reverse geocoding is unreachable; trying Photon.");
        }

        var arcGisJson = await TryGetArcGisJsonAsync(
            client,
            BuildArcGisReverseUrl(latText, lonText),
            reverse: true,
            cancellationToken);
        if (arcGisJson is not null)
            return Content(arcGisJson, "application/json");

        var photonJson = await TryGetPhotonJsonAsync(
            client,
            BuildPhotonReverseUrl(latText, lonText),
            reverse: true,
            cancellationToken);
        if (photonJson is not null)
            return Content(photonJson, "application/json");

        return StatusCode(502, new
        {
            message = "Dịch vụ chưa tìm được tên địa chỉ cho vị trí này. Tọa độ hiện tại vẫn được giữ."
        });
    }

    private HttpClient CreateProviderClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SmartCar2/1.0 (+https://github.com/hungnmph50619/SmartCar2)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en;q=0.7");
        client.Timeout = ProviderTimeout;
        return client;
    }

    private static string BuildNominatimSearchUrl(string query, double? nearLat, double? nearLon)
    {
        var url =
            $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&countrycodes=vn&q={Uri.EscapeDataString(query)}";
        if (!IsValidPoint(nearLat, nearLon))
            return url;

        var viewbox = string.Join(",", new[]
        {
            Math.Max(-180, nearLon!.Value - 0.55),
            Math.Max(-90, nearLat!.Value - 0.45),
            Math.Min(180, nearLon.Value + 0.55),
            Math.Min(90, nearLat.Value + 0.45)
        }.Select(value => value.ToString("0.#####", CultureInfo.InvariantCulture)));
        return $"{url}&viewbox={Uri.EscapeDataString(viewbox)}";
    }

    private static string BuildArcGisSearchUrl(string query, double? nearLat, double? nearLon)
    {
        var url =
            "https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/findAddressCandidates" +
            "?f=json&forStorage=false&outSR=4326&countryCode=VNM&maxLocations=8" +
            "&outFields=Match_addr%2CLongLabel" +
            $"&SingleLine={Uri.EscapeDataString(query)}";

        if (IsValidPoint(nearLat, nearLon))
        {
            var lat = nearLat!.Value.ToString("0.#####", CultureInfo.InvariantCulture);
            var lon = nearLon!.Value.ToString("0.#####", CultureInfo.InvariantCulture);
            url += $"&location={Uri.EscapeDataString($"{lon},{lat}")}&distance=50000";
        }

        return url;
    }

    private static string BuildArcGisReverseUrl(string lat, string lon) =>
        "https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/reverseGeocode" +
        $"?f=json&outSR=4326&langCode=vi&location={Uri.EscapeDataString($"{lon},{lat}")}";

    private static string BuildPhotonSearchUrl(string query, double? nearLat, double? nearLon)
    {
        var url = "https://photon.komoot.io/api?limit=8&lang=vi&countrycode=VN";
        if (IsValidPoint(nearLat, nearLon))
        {
            var lat = nearLat!.Value.ToString("0.#####", CultureInfo.InvariantCulture);
            var lon = nearLon!.Value.ToString("0.#####", CultureInfo.InvariantCulture);
            url += $"&lat={lat}&lon={lon}&zoom=13&location_bias_scale=0.2";
        }

        return $"{url}&q={Uri.EscapeDataString(query)}";
    }

    private static string BuildPhotonReverseUrl(string lat, string lon) =>
        $"https://photon.komoot.io/reverse?limit=1&radius=0.1&lang=vi&lat={lat}&lon={lon}";

    private async Task<string?> TryGetArcGisJsonAsync(
        HttpClient client,
        string url,
        bool reverse,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ArcGIS geocoding returned HTTP {StatusCode} for {ReverseLookup} lookup.",
                    (int)response.StatusCode,
                    reverse ? "reverse" : "search");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return NormalizeArcGisResponse(json, reverse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "ArcGIS geocoding timed out for {ReverseLookup} lookup.",
                reverse ? "reverse" : "search");
            return null;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "ArcGIS geocoding is unreachable for {ReverseLookup} lookup.",
                reverse ? "reverse" : "search");
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "ArcGIS returned invalid JSON for {ReverseLookup} lookup.",
                reverse ? "reverse" : "search");
            return null;
        }
    }

    private static string? NormalizeArcGisResponse(string json, bool reverse)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            root.TryGetProperty("error", out _))
            return null;

        if (reverse)
        {
            if (!root.TryGetProperty("address", out var address) ||
                address.ValueKind != JsonValueKind.Object)
                return null;

            var displayName =
                ReadString(address, "LongLabel") ??
                ReadString(address, "Match_addr") ??
                ReadString(address, "Address");
            if (string.IsNullOrWhiteSpace(displayName))
                return null;

            double? lat = null;
            double? lon = null;
            if (root.TryGetProperty("location", out var location) &&
                location.ValueKind == JsonValueKind.Object)
            {
                if (location.TryGetProperty("y", out var y) && y.TryGetDouble(out var parsedLat))
                    lat = parsedLat;
                if (location.TryGetProperty("x", out var x) && x.TryGetDouble(out var parsedLon))
                    lon = parsedLon;
            }

            return JsonSerializer.Serialize(new
            {
                lat = lat?.ToString("0.#######", CultureInfo.InvariantCulture),
                lon = lon?.ToString("0.#######", CultureInfo.InvariantCulture),
                display_name = displayName,
                countrycode = "VN"
            });
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
            return null;

        var places = new List<object>();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object ||
                !candidate.TryGetProperty("location", out var location) ||
                location.ValueKind != JsonValueKind.Object ||
                !location.TryGetProperty("x", out var x) ||
                !location.TryGetProperty("y", out var y) ||
                !x.TryGetDouble(out var lon) ||
                !y.TryGetDouble(out var lat) ||
                !double.IsFinite(lat) || !double.IsFinite(lon))
                continue;

            var displayName = ReadString(candidate, "address");
            if (string.IsNullOrWhiteSpace(displayName) &&
                candidate.TryGetProperty("attributes", out var attributes) &&
                attributes.ValueKind == JsonValueKind.Object)
            {
                displayName =
                    ReadString(attributes, "Match_addr") ??
                    ReadString(attributes, "LongLabel");
            }

            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            places.Add(new
            {
                lat = lat.ToString("0.#######", CultureInfo.InvariantCulture),
                lon = lon.ToString("0.#######", CultureInfo.InvariantCulture),
                display_name = displayName,
                countrycode = "VN"
            });
        }

        return places.Count == 0 ? null : JsonSerializer.Serialize(places);
    }

    private async Task<string?> TryGetPhotonJsonAsync(
        HttpClient client,
        string url,
        bool reverse,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Photon geocoding returned HTTP {StatusCode} for {ReverseLookup} lookup.",
                    (int)response.StatusCode,
                    reverse ? "reverse" : "search");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return NormalizePhotonResponse(json, reverse);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Photon geocoding timed out for {ReverseLookup} lookup.", reverse ? "reverse" : "search");
            return null;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Photon geocoding is unreachable for {ReverseLookup} lookup.", reverse ? "reverse" : "search");
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Photon returned invalid JSON for {ReverseLookup} lookup.", reverse ? "reverse" : "search");
            return null;
        }
    }

    private static string? NormalizePhotonResponse(string json, bool reverse)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
            return null;

        var places = new List<PhotonPlace>();
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object ||
                !feature.TryGetProperty("geometry", out var geometry) ||
                geometry.ValueKind != JsonValueKind.Object ||
                !geometry.TryGetProperty("coordinates", out var coordinates) ||
                coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2 ||
                !coordinates[0].TryGetDouble(out var lon) ||
                !coordinates[1].TryGetDouble(out var lat) ||
                !double.IsFinite(lat) || !double.IsFinite(lon) ||
                lat is < -90 or > 90 || lon is < -180 or > 180)
                continue;

            if (!feature.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
                continue;

            var countryCode = ReadString(properties, "countrycode");
            if (!string.IsNullOrWhiteSpace(countryCode) &&
                !string.Equals(countryCode, "VN", StringComparison.OrdinalIgnoreCase))
                continue;

            var streetAddress = string.Join(" ", new[]
            {
                ReadString(properties, "housenumber"),
                ReadString(properties, "street")
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var displayName = string.Join(", ", new[]
            {
                ReadString(properties, "name"),
                streetAddress,
                ReadString(properties, "district"),
                ReadString(properties, "city"),
                ReadString(properties, "state"),
                ReadString(properties, "country")
            }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            places.Add(new PhotonPlace(
                lat.ToString("0.#######", CultureInfo.InvariantCulture),
                lon.ToString("0.#######", CultureInfo.InvariantCulture),
                displayName,
                countryCode));
        }

        if (reverse)
        {
            var first = places.FirstOrDefault();
            return first is null ? null : JsonSerializer.Serialize(first.ToApiObject());
        }

        return JsonSerializer.Serialize(places.Select(place => place.ToApiObject()));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasNominatimSearchResults(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                document.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasNominatimAddress(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                !document.RootElement.TryGetProperty("error", out _) &&
                !string.IsNullOrWhiteSpace(ReadString(document.RootElement, "display_name"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidPoint(double? lat, double? lon) =>
        lat.HasValue && lon.HasValue &&
        double.IsFinite(lat.Value) && double.IsFinite(lon.Value) &&
        lat.Value is >= -90 and <= 90 && lon.Value is >= -180 and <= 180;

    private sealed record PhotonPlace(string Lat, string Lon, string DisplayName, string? CountryCode)
    {
        public object ToApiObject() => new
        {
            lat = Lat,
            lon = Lon,
            display_name = DisplayName,
            countrycode = CountryCode
        };
    }
}
