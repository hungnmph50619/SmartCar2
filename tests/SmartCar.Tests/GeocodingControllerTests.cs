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
    public async Task Search_uses_server_side_photon_when_nominatim_cannot_connect()
    {
        var requests = new List<Uri>();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "nominatim.openstreetmap.org")
                throw new HttpRequestException("Nominatim is unreachable");

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
        Assert.Equal("21.0123", candidate.GetProperty("lat").GetString());
        Assert.Equal("105.9321", candidate.GetProperty("lon").GetString());
        Assert.Contains("134", candidate.GetProperty("display_name").GetString());
        Assert.Equal(2, requests.Count);
        Assert.Equal("photon.komoot.io", requests[1].Host);
        Assert.Contains("countrycode=VN", requests[1].Query);
        Assert.Contains("lat=21.03", requests[1].Query);
        Assert.Contains("lon=105.8", requests[1].Query);
    }

    [Fact]
    public async Task Reverse_uses_server_side_photon_when_nominatim_has_no_address()
    {
        var requests = new List<Uri>();
        var handler = new DelegateHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "nominatim.openstreetmap.org")
                return Task.FromResult(JsonResponse("{\"error\":\"Unable to geocode\"}", HttpStatusCode.NotFound));

            return Task.FromResult(JsonResponse("""
                {
                  "features": [
                    {
                      "geometry": { "coordinates": [105.8, 21.03] },
                      "properties": {
                        "countrycode": "VN",
                        "housenumber": "25",
                        "street": "Phố Huế",
                        "city": "Hà Nội",
                        "country": "Việt Nam"
                      }
                    }
                  ]
                }
                """));
        });
        var controller = CreateController(handler);

        var result = await controller.Reverse(21.03, 105.8, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        using var json = JsonDocument.Parse(content.Content!);
        Assert.Equal("25 Phố Huế, Hà Nội, Việt Nam", json.RootElement.GetProperty("display_name").GetString());
        Assert.Equal(2, requests.Count);
        Assert.Equal("photon.komoot.io", requests[1].Host);
        Assert.Contains("radius=0.1", requests[1].Query);
    }

    private static GeocodingController CreateController(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
