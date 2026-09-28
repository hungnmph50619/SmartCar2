using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SmartCar.Web.Controllers;
using Xunit;

namespace SmartCar.Tests;

public sealed class GeocodingControllerTests
{
    [Fact]
    public async Task Search_uses_server_side_arcgis_when_nominatim_cannot_connect()
    {
        var requests = new List<Uri>();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);

            if (request.RequestUri!.Host == "nominatim.openstreetmap.org")
                throw new HttpRequestException("Nominatim is unreachable");

            if (request.RequestUri.Host == "geocode.arcgis.com")
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "candidates": [
                        {
                          "address": "134 Đường Trâu Quỳ, Gia Lâm, Hà Nội",
                          "location": { "x": 105.9321, "y": 21.0123 },
                          "attributes": {
                            "Match_addr": "134 Đường Trâu Quỳ, Gia Lâm, Hà Nội"
                          }
                        }
                      ]
                    }
                    """));
            }

            throw new InvalidOperationException($"Unexpected host {request.RequestUri.Host}");
        });
        var controller = CreateController(handler);

        var result = await controller.Search(
            "Ngõ 134 đường Trâu Quỳ", 21.03, 105.8, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        using var json = JsonDocument.Parse(content.Content!);
        var candidate = Assert.Single(json.RootElement.EnumerateArray());

        Assert.Equal("21.0123", candidate.GetProperty("lat").GetString());
        Assert.Equal("105.9321", candidate.GetProperty("lon").GetString());
        Assert.Contains("134", candidate.GetProperty("display_name").GetString());
        Assert.Equal(2, requests.Count);
        Assert.Equal("geocode.arcgis.com", requests[1].Host);
        Assert.Contains("countryCode=VNM", requests[1].Query);
        Assert.Contains("location=105.8%2C21.03", requests[1].Query);
    }

    [Fact]
    public async Task Reverse_uses_server_side_arcgis_when_nominatim_has_no_address()
    {
        var requests = new List<Uri>();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);

            if (request.RequestUri!.Host == "nominatim.openstreetmap.org")
                return Task.FromResult(JsonResponse(
                    """{"error":"Unable to geocode"}""",
                    HttpStatusCode.NotFound));

            if (request.RequestUri.Host == "geocode.arcgis.com")
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "address": {
                        "LongLabel": "25 Phố Huế, Hà Nội, Việt Nam",
                        "Match_addr": "25 Phố Huế, Hà Nội"
                      },
                      "location": { "x": 105.8, "y": 21.03 }
                    }
                    """));
            }

            throw new InvalidOperationException($"Unexpected host {request.RequestUri.Host}");
        });
        var controller = CreateController(handler);

        var result = await controller.Reverse(21.03, 105.8, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        using var json = JsonDocument.Parse(content.Content!);

        Assert.Equal(
            "25 Phố Huế, Hà Nội, Việt Nam",
            json.RootElement.GetProperty("display_name").GetString());
        Assert.Equal("21.03", json.RootElement.GetProperty("lat").GetString());
        Assert.Equal("105.8", json.RootElement.GetProperty("lon").GetString());
        Assert.Equal(2, requests.Count);
        Assert.Equal("geocode.arcgis.com", requests[1].Host);
        Assert.Contains("location=105.8%2C21.03", requests[1].Query);
    }

    [Fact]
    public async Task Search_still_falls_back_to_photon_when_nominatim_and_arcgis_fail()
    {
        var requests = new List<Uri>();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);

            if (request.RequestUri!.Host == "nominatim.openstreetmap.org")
                throw new HttpRequestException("Nominatim is unreachable");

            if (request.RequestUri.Host == "geocode.arcgis.com")
                throw new HttpRequestException("ArcGIS is unreachable");

            return Task.FromResult(JsonResponse("""
                {
                  "features": [
                    {
                      "geometry": { "coordinates": [105.9321, 21.0123] },
                      "properties": {
                        "countrycode": "VN",
                        "housenumber": "134",
                        "street": "Đường Trâu Quỳ",
                        "district": "Gia Lâm",
                        "city": "Hà Nội",
                        "country": "Việt Nam"
                      }
                    }
                  ]
                }
                """));
        });
        var controller = CreateController(handler);

        var result = await controller.Search(
            "Ngõ 134 đường Trâu Quỳ", 21.03, 105.8, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        using var json = JsonDocument.Parse(content.Content!);
        var candidate = Assert.Single(json.RootElement.EnumerateArray());

        Assert.Contains("134", candidate.GetProperty("display_name").GetString());
        Assert.Equal(3, requests.Count);
        Assert.Equal("photon.komoot.io", requests[2].Host);
    }

    private static GeocodingController CreateController(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler));

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request);
    }
}
