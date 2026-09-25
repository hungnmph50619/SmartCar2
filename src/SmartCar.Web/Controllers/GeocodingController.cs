using System.Globalization;
using System.Net;
using System.Text.Json;
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

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var query = q?.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 3 || query.Length > 200)
            return BadRequest(new { message = "Nhập địa chỉ từ 3 đến 200 ký tự." });

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SmartCar2/1.0 (+https://github.com/hungnmph50619/SmartCar2)");
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en;q=0.7");
        var url = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=vn&q={Uri.EscapeDataString(query)}";
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return StatusCode(503, new { message = "Dịch vụ địa chỉ đang giới hạn lượt tìm. Vui lòng thử lại sau." });
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new { message = $"Dịch vụ tìm địa chỉ trả lỗi {(int)response.StatusCode}. Vui lòng thử lại sau." });
            return Content(await response.Content.ReadAsStringAsync(cancellationToken), "application/json");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new { message = "Tìm địa chỉ quá thời gian." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { message = "Không thể kết nối dịch vụ bản đồ." });
        }
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SmartCar2/1.0 (+https://github.com/hungnmph50619/SmartCar2)");
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en;q=0.7");

        var latText = lat.ToString("0.#######", CultureInfo.InvariantCulture);
        var lonText = lon.ToString("0.#######", CultureInfo.InvariantCulture);
        var url =
            $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi&lat={latText}&lon={lonText}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return StatusCode(503, new { message = "Dịch vụ địa chỉ đang giới hạn lượt tra. Tọa độ đã lưu; vui lòng nhập địa chỉ thủ công hoặc thử lại sau." });
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new { message = $"Dịch vụ địa chỉ trả lỗi {(int)response.StatusCode}. Tọa độ vẫn dùng để tính phí." });

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("error", out _))
                return NotFound(new { message = "Không có địa chỉ trong dữ liệu bản đồ tại tọa độ này. Vui lòng nhập địa chỉ thủ công." });
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
