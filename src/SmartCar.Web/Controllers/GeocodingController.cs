using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace SmartCar.Web.Controllers;

[Route("api/geocoding")]
public sealed class GeocodingController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GeocodingController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("reverse")]
    public async Task<IActionResult> Reverse(
        [FromQuery] double lat,
        [FromQuery] double lon,
        CancellationToken cancellationToken)
    {
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            return BadRequest(new { message = "Tọa độ không hợp lệ." });
        }

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartCar/1.0 (student project)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en;q=0.7");

        var latText = lat.ToString("0.#######", CultureInfo.InvariantCulture);
        var lonText = lon.ToString("0.#######", CultureInfo.InvariantCulture);
        var url =
            $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi&lat={latText}&lon={lonText}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(502, new { message = "Không thể tra tên địa điểm lúc này." });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Content(json, "application/json");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new { message = "Tra địa chỉ quá thời gian." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { message = "Không thể kết nối dịch vụ bản đồ." });
        }
    }
}
