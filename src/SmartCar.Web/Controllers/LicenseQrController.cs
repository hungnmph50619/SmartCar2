using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;
using ZXing;
using ZXing.Common;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class LicenseQrController : Controller
{
    private const long MaximumDocumentImageBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> ValidClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "A1", "A2", "A3", "A4", "B", "B1", "B2", "C1", "C", "D1", "D2", "D",
        "BE", "C1E", "CE", "D1E", "D2E", "DE", "E", "FB2", "FC", "FD", "FE"
    };

    [HttpPost("/Ekyc/PreviewLicenseQr")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewLicenseQr(
        IFormFile? frontImage,
        IFormFile? backImage,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var frontError = await ImageFileValidator.ValidateAsync(frontImage, MaximumDocumentImageBytes, cancellationToken);
        var backError = await ImageFileValidator.ValidateAsync(backImage, MaximumDocumentImageBytes, cancellationToken);
        if (frontError is not null) errors.Add($"Mặt trước: {frontError}");
        if (backError is not null) errors.Add($"Mặt sau: {backError}");
        if (frontImage is not null && backImage is not null &&
            await ImageFileValidator.HaveSameContentAsync(frontImage, backImage, cancellationToken))
        {
            errors.Add("Ảnh mặt trước và mặt sau GPLX phải là hai ảnh khác nhau.");
        }

        if (errors.Count > 0 || frontImage is null || backImage is null)
        {
            return BadRequest(new { succeeded = false, errors });
        }

        string? payload = null;
        string? sourceSide = null;
        try
        {
            // GPLX PET thường đặt QR ở một trong hai mặt. Ưu tiên mặt sau rồi thử mặt trước.
            payload = DecodeQrPayload(backImage);
            sourceSide = string.IsNullOrWhiteSpace(payload) ? null : "back";
            if (string.IsNullOrWhiteSpace(payload))
            {
                payload = DecodeQrPayload(frontImage);
                sourceSide = string.IsNullOrWhiteSpace(payload) ? null : "front";
            }
        }
        catch
        {
            payload = null;
            sourceSide = null;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return Json(new
            {
                succeeded = true,
                qrDecoded = false,
                extractedCount = 0,
                message = "Không đọc được QR GPLX. SmartCar sẽ chuyển sang OCR cục bộ để hỗ trợ điền thông tin."
            });
        }

        var parsed = ParseLicensePayload(payload);
        var extractedCount = new object?[]
        {
            parsed.FullName,
            parsed.DocumentNumber,
            parsed.LicenseClass,
            parsed.IssuedDate,
            parsed.ExpiryDate
        }.Count(value => value is not null && !string.IsNullOrWhiteSpace(value.ToString()));

        return Json(new
        {
            succeeded = true,
            qrDecoded = true,
            sourceSide,
            extractedCount,
            parsed.FullName,
            parsed.DocumentNumber,
            parsed.LicenseClass,
            issuedDate = parsed.IssuedDate?.ToString("dd/MM/yyyy"),
            expiryDate = parsed.ExpiryDate?.ToString("dd/MM/yyyy"),
            message = extractedCount > 0
                ? $"Đã đọc QR GPLX và lấy được {extractedCount}/5 trường. Các trường còn thiếu sẽ dùng OCR cục bộ."
                : "Đã đọc được QR GPLX nhưng cấu trúc mã không chứa các trường SmartCar có thể tự tách. Hệ thống sẽ dùng OCR cục bộ cho phần còn thiếu."
        });
    }

    private static string? DecodeQrPayload(IFormFile imageFile)
    {
        using var stream = imageFile.OpenReadStream();
        using var image = Image.Load<Rgba32>(stream);

        var reader = new BarcodeReader<Rgba32>
        {
            AutoRotate = true,
            TryInverted = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        var direct = reader.Decode(image);
        if (!string.IsNullOrWhiteSpace(direct?.Text))
        {
            return direct.Text.Trim();
        }

        foreach (var tile in BuildQrTiles(image.Width, image.Height))
        {
            using var cropped = image.Clone(context =>
            {
                context.Crop(tile);
                var longSide = Math.Max(tile.Width, tile.Height);
                var scale = Math.Clamp(1600d / Math.Max(1, longSide), 1.2d, 3d);
                context.Resize(
                    Math.Max(1, (int)Math.Round(tile.Width * scale)),
                    Math.Max(1, (int)Math.Round(tile.Height * scale)));
            });

            var decoded = reader.Decode(cropped);
            if (!string.IsNullOrWhiteSpace(decoded?.Text))
            {
                return decoded.Text.Trim();
            }
        }

        return null;
    }

    private static IEnumerable<Rectangle> BuildQrTiles(int width, int height)
    {
        var tileWidth = Math.Max(1, (int)Math.Round(width * 0.58));
        var tileHeight = Math.Max(1, (int)Math.Round(height * 0.58));
        var xPositions = new[] { 0, Math.Max(0, (width - tileWidth) / 2), Math.Max(0, width - tileWidth) };
        var yPositions = new[] { 0, Math.Max(0, (height - tileHeight) / 2), Math.Max(0, height - tileHeight) };

        foreach (var y in yPositions.Distinct())
        {
            foreach (var x in xPositions.Distinct())
            {
                yield return new Rectangle(
                    x,
                    y,
                    Math.Min(tileWidth, width - x),
                    Math.Min(tileHeight, height - y));
            }
        }
    }

    private static LicenseQrData ParseLicensePayload(string payload)
    {
        var clean = payload.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        var tokens = clean
            .Split(new[] { '|', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeSpaces)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        string? fullName = FindLabeledValue(clean, new[] { "HO VA TEN", "HO TEN", "FULL NAME", "NAME" });
        string? documentNumber = FindLabeledValue(clean, new[] { "SO GPLX", "GPLX", "LICENSE NUMBER", "LICENCE NUMBER", "SO", "NO" });
        string? licenseClass = FindLabeledValue(clean, new[] { "HANG", "CLASS" });
        var issued = FindLabeledDate(clean, new[] { "NGAY CAP", "DATE OF ISSUE", "ISSUED", "ISSUE DATE" });
        var expiry = FindLabeledDate(clean, new[] { "CO GIA TRI DEN", "DATE OF EXPIRY", "EXPIRY", "EXPIRES", "VALID TO" });

        documentNumber = CleanDocumentNumber(documentNumber);
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            documentNumber = tokens
                .Select(CleanDocumentNumber)
                .FirstOrDefault(value => value is not null && value.Length is >= 8 and <= 15 &&
                                         value.Count(char.IsDigit) >= 8 && !LooksLikeDateDigits(value));
        }

        licenseClass = CleanClass(licenseClass);
        if (string.IsNullOrWhiteSpace(licenseClass))
        {
            licenseClass = tokens.Select(CleanClass).FirstOrDefault(value => value is not null);
        }

        fullName = CleanName(fullName);
        if (string.IsNullOrWhiteSpace(fullName))
        {
            fullName = tokens.Select(CleanName).FirstOrDefault(IsLikelyName);
        }

        if (!issued.HasValue || !expiry.HasValue)
        {
            var dates = ExtractDates(clean).Distinct().OrderBy(value => value).ToList();
            if (!issued.HasValue && dates.Count >= 2)
            {
                issued = dates.FirstOrDefault(value => value.Year >= 2000 && value.Date <= DateTime.Today);
            }
            if (!expiry.HasValue && dates.Count >= 2)
            {
                expiry = dates.LastOrDefault(value => value.Date > DateTime.Today.AddYears(-2));
            }
        }

        return new LicenseQrData(fullName, documentNumber, licenseClass, issued, expiry);
    }

    private static string? FindLabeledValue(string payload, IEnumerable<string> labels)
    {
        var normalized = RemoveDiacritics(payload).ToUpperInvariant();
        foreach (var label in labels)
        {
            var pattern = $@"(?:^|[|;\r\n])\s*{Regex.Escape(label)}\s*[:=\-/]?\s*([^|;\r\n]{{1,80}})";
            var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var rawIndex = Math.Min(payload.Length, match.Index + Math.Max(0, match.Value.IndexOf(match.Groups[1].Value, StringComparison.Ordinal)));
                var rawLength = Math.Min(match.Groups[1].Length, Math.Max(0, payload.Length - rawIndex));
                return rawLength > 0 ? payload.Substring(rawIndex, rawLength).Trim() : match.Groups[1].Value.Trim();
            }
        }
        return null;
    }

    private static DateTime? FindLabeledDate(string payload, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var value = FindLabeledValue(payload, new[] { label });
            var date = ParseDate(value);
            if (date.HasValue) return date;
        }
        return null;
    }

    private static IEnumerable<DateTime> ExtractDates(string value)
    {
        foreach (Match match in Regex.Matches(value, @"\b(?:[0-3]?\d)[/\-.](?:[01]?\d)[/\-.](?:19|20)\d{2}\b"))
        {
            var date = ParseDate(match.Value);
            if (date.HasValue) yield return date.Value;
        }
        foreach (Match match in Regex.Matches(value, @"\b\d{8}\b"))
        {
            var date = ParseDate(match.Value);
            if (date.HasValue) yield return date.Value;
        }
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy", "ddMMyyyy", "yyyyMMdd" };
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date.Date;
            }
        }

        var match = Regex.Match(value, @"([0-3]?\d)[/\-.]([01]?\d)[/\-.]((?:19|20)\d{2})");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var day) &&
            int.TryParse(match.Groups[2].Value, out var month) && int.TryParse(match.Groups[3].Value, out var year))
        {
            try { return new DateTime(year, month, day); } catch { return null; }
        }
        return null;
    }

    private static string? CleanDocumentNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = Regex.Replace(RemoveDiacritics(value).ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
        if (compact.Length is < 8 or > 20) return null;
        if (compact.All(char.IsLetter)) return null;
        return compact;
    }

    private static bool LooksLikeDateDigits(string value)
    {
        if (value.Length != 8 || !value.All(char.IsDigit)) return false;
        return ParseDate(value) is not null;
    }

    private static string? CleanClass(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = RemoveDiacritics(value).ToUpperInvariant();
        foreach (Match match in Regex.Matches(normalized, @"\b[A-Z]{1,3}\d?[A-Z]?\b"))
        {
            if (ValidClasses.Contains(match.Value)) return match.Value;
        }
        return null;
    }

    private static string? CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = Regex.Replace(value, @"[^\p{L}'\-\s]", " ");
        return NormalizeSpaces(cleaned);
    }

    private static bool IsLikelyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 7) return false;
        var normalized = RemoveDiacritics(value).ToUpperInvariant();
        return !new[]
        {
            "GIAY PHEP", "DRIVER", "CONG HOA", "QUOC TICH", "NATIONALITY", "DIA CHI", "ADDRESS",
            "HANG", "CLASS", "CO GIA TRI", "EXPIRES", "VEHICLE"
        }.Any(normalized.Contains);
    }

    private static string NormalizeSpaces(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character is 'đ' ? 'd' : character is 'Đ' ? 'D' : character);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed record LicenseQrData(
        string? FullName,
        string? DocumentNumber,
        string? LicenseClass,
        DateTime? IssuedDate,
        DateTime? ExpiryDate);
}
