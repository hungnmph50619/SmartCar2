using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure.Identity;
using SmartCar.Web.Services;
using ZXing;
using ZXing.Common;
using ZXing.ImageSharp;

namespace SmartCar.Web.Controllers;

[Authorize(Roles = RoleNames.Customer)]
public sealed class CitizenQrController : Controller
{
    private const long MaximumDocumentImageBytes = 5 * 1024 * 1024;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEkycResultStore _ekycResultStore;

    public CitizenQrController(
        UserManager<ApplicationUser> userManager,
        IEkycResultStore ekycResultStore)
    {
        _userManager = userManager;
        _ekycResultStore = ekycResultStore;
    }

    [HttpPost("/Ekyc/PreviewCitizenQrMrz")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewCitizenQrMrz(
        IFormFile? frontImage,
        IFormFile? backImage,
        string? mrzText,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var imageErrors = await ValidateDocumentImagesAsync(frontImage, backImage, cancellationToken);
        if (imageErrors.Count > 0 || frontImage is null || backImage is null)
        {
            return BadRequest(new { succeeded = false, errors = imageErrors });
        }

        string? qrPayload;
        try
        {
            qrPayload = DecodeQrPayload(frontImage);
        }
        catch
        {
            qrPayload = null;
        }

        if (string.IsNullOrWhiteSpace(qrPayload))
        {
            return BadRequest(new
            {
                succeeded = false,
                code = "QR_NOT_READABLE",
                errors = new[]
                {
                    "Không đọc được mã QR ở mặt trước CCCD. Hãy chụp gần hơn, tránh lóa và để mã QR rõ nét; nếu vẫn không đọc được, hãy chuyển sang nhập thủ công."
                }
            });
        }

        var qr = TryParseCitizenQr(qrPayload);
        if (qr is null)
        {
            return BadRequest(new
            {
                succeeded = false,
                code = "QR_NOT_CITIZEN_ID",
                errors = new[]
                {
                    "Mã QR không có cấu trúc CCCD mà SmartCar hỗ trợ. Hãy kiểm tra đúng mặt trước CCCD hoặc chuyển sang nhập thủ công."
                }
            });
        }

        CitizenMrzData? mrz = null;
        if (!string.IsNullOrWhiteSpace(mrzText))
        {
            mrz = TryParseCitizenMrz(mrzText);
        }

        if (mrz is null)
        {
            return BadRequest(new
            {
                succeeded = false,
                code = "MRZ_NOT_READABLE",
                errors = new[]
                {
                    "Đã đọc được QR mặt trước nhưng chưa đọc được dòng MRZ ở mặt sau. Hãy chụp mặt sau gần hơn, rõ phần 3 dòng ký tự ở cuối thẻ; nếu vẫn không đọc được, hãy nhập thủ công."
                }
            });
        }

        var mismatchErrors = new List<string>();
        if (qr.DateOfBirth.HasValue && mrz.DateOfBirth.HasValue &&
            qr.DateOfBirth.Value.Date != mrz.DateOfBirth.Value.Date)
        {
            mismatchErrors.Add("Ngày sinh trong QR mặt trước không khớp với MRZ mặt sau.");
        }

        if (!string.IsNullOrWhiteSpace(qr.Gender) && !string.IsNullOrWhiteSpace(mrz.Gender) &&
            !string.Equals(qr.Gender, mrz.Gender, StringComparison.OrdinalIgnoreCase))
        {
            mismatchErrors.Add("Giới tính trong QR mặt trước không khớp với MRZ mặt sau.");
        }

        if (mismatchErrors.Count > 0)
        {
            mismatchErrors.Add("Hai mặt CCCD có thông tin không khớp. Hãy kiểm tra lại ảnh mặt trước và mặt sau.");
            return BadRequest(new
            {
                succeeded = false,
                code = "QR_MRZ_MISMATCH",
                errors = mismatchErrors
            });
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var checkedAt = DateTime.UtcNow;
        var result = new EkycOcrResult(
            Succeeded: true,
            IsDemo: false,
            Provider: "CCCD QR + MRZ local",
            SessionId: sessionId,
            DocumentNumber: qr.DocumentNumber,
            FullName: qr.FullName,
            DateOfBirth: qr.DateOfBirth ?? mrz.DateOfBirth,
            Gender: qr.Gender ?? mrz.Gender,
            IssuedDate: qr.IssuedDate,
            ExpiryDate: mrz.ExpiryDate,
            Address: qr.Address,
            LicenseClass: null,
            OcrConfidence: 99m,
            Message: "Đã giải mã QR mặt trước và đối chiếu MRZ mặt sau. Đây là kiểm tra dữ liệu trên ảnh, không phải xác thực với cơ sở dữ liệu nhà nước.",
            CheckedAtUtc: checkedAt);

        await _ekycResultStore.SaveOcrAsync(user.Id, result, cancellationToken);

        return Json(new
        {
            succeeded = true,
            isDemo = false,
            provider = result.Provider,
            sessionId,
            result.DocumentNumber,
            result.FullName,
            dateOfBirth = result.DateOfBirth?.ToString("dd/MM/yyyy"),
            result.Gender,
            issuedDate = result.IssuedDate?.ToString("dd/MM/yyyy"),
            expiryDate = result.ExpiryDate?.ToString("dd/MM/yyyy"),
            result.Address,
            ocrConfidence = result.OcrConfidence,
            result.Message,
            qrDecoded = true,
            mrzMatched = true
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
                var scale = Math.Clamp(1800d / Math.Max(1, longSide), 1.4d, 3d);
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
        var tileWidth = Math.Max(1, (int)Math.Round(width * 0.62));
        var tileHeight = Math.Max(1, (int)Math.Round(height * 0.62));
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

    private static CitizenQrData? TryParseCitizenQr(string payload)
    {
        var parts = payload
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split('|', StringSplitOptions.None)
            .Select(value => value.Trim())
            .ToList();

        if (parts.Count < 6)
        {
            return null;
        }

        var idIndex = parts.FindIndex(value => DigitsOnly(value).Length == 12);
        if (idIndex < 0)
        {
            return null;
        }

        var documentNumber = DigitsOnly(parts[idIndex]);
        var cursor = idIndex + 1;

        if (cursor < parts.Count && IsLegacyIdentityNumber(parts[cursor]))
        {
            cursor += 1;
        }

        if (cursor >= parts.Count)
        {
            return null;
        }

        var fullName = NormalizeSpaces(parts[cursor]);
        cursor += 1;
        if (fullName.Length < 4 || fullName.Any(char.IsDigit))
        {
            return null;
        }

        DateTime? dateOfBirth = null;
        if (cursor < parts.Count)
        {
            dateOfBirth = ParseQrDate(parts[cursor]);
            if (dateOfBirth.HasValue)
            {
                cursor += 1;
            }
        }

        string? gender = null;
        if (cursor < parts.Count)
        {
            gender = NormalizeGender(parts[cursor]);
            if (gender is not null)
            {
                cursor += 1;
            }
        }

        DateTime? issuedDate = null;
        var issueIndex = -1;
        for (var index = parts.Count - 1; index >= cursor; index -= 1)
        {
            var parsed = ParseQrDate(parts[index]);
            if (!parsed.HasValue)
            {
                continue;
            }

            issuedDate = parsed;
            issueIndex = index;
            break;
        }

        var addressEnd = issueIndex >= cursor ? issueIndex : parts.Count;
        var address = NormalizeSpaces(string.Join(", ", parts.Skip(cursor).Take(addressEnd - cursor).Where(value => !string.IsNullOrWhiteSpace(value))));
        if (address.Length < 4)
        {
            address = null;
        }

        return new CitizenQrData(
            documentNumber,
            fullName,
            dateOfBirth,
            gender,
            issuedDate,
            address);
    }

    private static CitizenMrzData? TryParseCitizenMrz(string rawText)
    {
        var lines = rawText
            .Replace('«', '<')
            .ToUpperInvariant()
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray()))
            .Where(value => value.Length >= 10)
            .ToList();

        foreach (var line in lines)
        {
            var vnmIndex = line.IndexOf("VNM", StringComparison.Ordinal);
            if (vnmIndex < 0)
            {
                continue;
            }

            var prefix = line[..vnmIndex];
            var sexIndex = Math.Max(prefix.LastIndexOf('F'), prefix.LastIndexOf('M'));
            if (sexIndex < 7 || prefix.Length < sexIndex + 8)
            {
                continue;
            }

            var birthBlock = prefix.Substring(sexIndex - 7, 7);
            var expiryBlock = prefix.Substring(sexIndex + 1, Math.Min(7, prefix.Length - sexIndex - 1));
            if (expiryBlock.Length < 7)
            {
                continue;
            }

            var birthDigits = NormalizeMrzDigits(birthBlock[..6]);
            var birthCheck = NormalizeMrzDigit(birthBlock[6]);
            var expiryDigits = NormalizeMrzDigits(expiryBlock[..6]);
            var expiryCheck = NormalizeMrzDigit(expiryBlock[6]);

            if (birthDigits.Length != 6 || expiryDigits.Length != 6 ||
                birthCheck is null || expiryCheck is null)
            {
                continue;
            }

            if (MrzCheckDigit(birthDigits) != birthCheck.Value ||
                MrzCheckDigit(expiryDigits) != expiryCheck.Value)
            {
                continue;
            }

            var birthDate = ParseMrzDate(birthDigits, isExpiry: false);
            var expiryDate = ParseMrzDate(expiryDigits, isExpiry: true);
            if (!birthDate.HasValue || !expiryDate.HasValue)
            {
                continue;
            }

            return new CitizenMrzData(
                birthDate,
                line[sexIndex] == 'F' ? "Nữ" : "Nam",
                expiryDate);
        }

        return null;
    }

    private static DateTime? ParseQrDate(string value)
    {
        var digits = DigitsOnly(value);
        if (digits.Length != 8)
        {
            return null;
        }

        var candidates = new[]
        {
            (Day: digits[..2], Month: digits.Substring(2, 2), Year: digits[4..]),
            (Day: digits.Substring(6, 2), Month: digits.Substring(4, 2), Year: digits[..4])
        };

        foreach (var candidate in candidates)
        {
            if (int.TryParse(candidate.Day, out var day) &&
                int.TryParse(candidate.Month, out var month) &&
                int.TryParse(candidate.Year, out var year) &&
                TryCreateDate(year, month, day, out var date))
            {
                return date;
            }
        }

        return null;
    }

    private static DateTime? ParseMrzDate(string value, bool isExpiry)
    {
        if (value.Length != 6 || !value.All(char.IsDigit))
        {
            return null;
        }

        var yy = int.Parse(value[..2]);
        var month = int.Parse(value.Substring(2, 2));
        var day = int.Parse(value.Substring(4, 2));
        var currentTwoDigitYear = DateTime.UtcNow.Year % 100;
        var year = isExpiry
            ? 2000 + yy
            : yy > currentTwoDigitYear ? 1900 + yy : 2000 + yy;

        return TryCreateDate(year, month, day, out var date) ? date : null;
    }

    private static bool TryCreateDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        if (year < 1900 || year > 2199 || month is < 1 or > 12)
        {
            return false;
        }

        var days = DateTime.DaysInMonth(year, month);
        if (day < 1 || day > days)
        {
            return false;
        }

        date = new DateTime(year, month, day);
        return true;
    }

    private static int MrzCheckDigit(string value)
    {
        var weights = new[] { 7, 3, 1 };
        var sum = 0;
        for (var index = 0; index < value.Length; index += 1)
        {
            sum += (value[index] - '0') * weights[index % weights.Length];
        }
        return sum % 10;
    }

    private static string NormalizeMrzDigits(string value) =>
        new(value.Select(character => NormalizeMrzDigit(character)?.ToString()[0] ?? '\0')
            .Where(character => character != '\0')
            .ToArray());

    private static int? NormalizeMrzDigit(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        'O' => 0,
        'I' or 'L' => 1,
        'Z' => 2,
        'S' => 5,
        'B' => 8,
        _ => null
    };

    private static bool IsLegacyIdentityNumber(string value)
    {
        var digits = DigitsOnly(value);
        return digits.Length is 9 or 12;
    }

    private static string? NormalizeGender(string value)
    {
        var normalized = RemoveDiacritics(value).ToUpperInvariant();
        if (normalized is "NAM" or "M" or "MALE") return "Nam";
        if (normalized is "NU" or "F" or "FEMALE") return "Nữ";
        return null;
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) !=
                                System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(character => character is 'đ' or 'Đ' ? 'D' : character)
            .ToArray();
        return new string(chars).Normalize(System.Text.NormalizationForm.FormC);
    }

    private static string DigitsOnly(string value) =>
        new(value.Where(char.IsDigit).ToArray());

    private static string NormalizeSpaces(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<List<string>> ValidateDocumentImagesAsync(
        IFormFile? frontImage,
        IFormFile? backImage,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var frontError = await ImageFileValidator.ValidateAsync(frontImage, MaximumDocumentImageBytes, cancellationToken);
        if (frontError is not null) errors.Add($"Mặt trước: {frontError}");
        var backError = await ImageFileValidator.ValidateAsync(backImage, MaximumDocumentImageBytes, cancellationToken);
        if (backError is not null) errors.Add($"Mặt sau: {backError}");
        return errors;
    }

    private sealed record CitizenQrData(
        string DocumentNumber,
        string FullName,
        DateTime? DateOfBirth,
        string? Gender,
        DateTime? IssuedDate,
        string? Address);

    private sealed record CitizenMrzData(
        DateTime? DateOfBirth,
        string? Gender,
        DateTime? ExpiryDate);
}
