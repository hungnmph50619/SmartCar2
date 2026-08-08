using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace SmartCar.Web.Services;

public static class ImageFileValidator
{
    private static readonly IReadOnlyDictionary<string, string> AllowedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

    public static async Task<string?> ValidateAsync(
        IFormFile? file,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return "Vui lòng chọn ảnh.";
        }

        if (file.Length > maximumBytes)
        {
            return $"Ảnh không được vượt quá {maximumBytes / 1024 / 1024} MB.";
        }

        var extension = Path.GetExtension(file.FileName);
        if (!AllowedContentTypes.TryGetValue(extension, out var expectedContentType))
        {
            return "Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.";
        }

        if (!string.Equals(file.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            return "Loại nội dung của file không khớp với định dạng ảnh.";
        }

        var header = new byte[12];
        await using var stream = file.OpenReadStream();
        var bytesRead = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);

        var validSignature = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => bytesRead >= 3 &&
                header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => bytesRead >= 8 &&
                header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A,
            ".webp" => bytesRead >= 12 &&
                header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50,
            _ => false
        };

        return validSignature ? null : "Nội dung file không phải là ảnh hợp lệ.";
    }

    public static async Task<bool> HaveSameContentAsync(
        IFormFile? first,
        IFormFile? second,
        CancellationToken cancellationToken = default)
    {
        if (first is null || second is null ||
            first.Length <= 0 || second.Length <= 0 ||
            first.Length != second.Length)
        {
            return false;
        }

        await using var firstStream = first.OpenReadStream();
        await using var secondStream = second.OpenReadStream();

        var firstHash = await SHA256.HashDataAsync(firstStream, cancellationToken);
        var secondHash = await SHA256.HashDataAsync(secondStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    public static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
