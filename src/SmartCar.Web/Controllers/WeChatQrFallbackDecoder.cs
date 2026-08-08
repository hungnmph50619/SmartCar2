using System.Diagnostics;
using System.Security.Cryptography;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;

namespace SmartCar.Web.Controllers;

/// <summary>
/// Fallback QR decoder for difficult CCCD photos.
///
/// ZXing remains the fast first choice.  This fallback is only invoked after
/// the lightweight decoder has failed.  On Windows it uses OpenCV's
/// WeChatQRCode implementation.  The official WeChat detector and
/// super-resolution model files are downloaded once, checksum-verified and
/// cached under the current user's LocalAppData folder.
/// </summary>
internal static class WeChatQrFallbackDecoder
{
    private const string ModelCommit = "3487ef7cde71d93c6a01bb0b84aa0f22c6128f6b";
    private const string ModelBaseUrl =
        "https://raw.githubusercontent.com/WeChatCV/opencv_3rdparty/" + ModelCommit + "/";

    private static readonly object ModelSync = new();
    private static readonly HttpClient ModelClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly ModelSpec[] ModelSpecs =
    {
        new("detect.prototxt", "6fb4976b32695f9f5c6305c19f12537d"),
        new("detect.caffemodel", "238e2b2d6f3c18d6c3a30de0c31e23cf"),
        new("sr.prototxt", "69db99927a70df953b471daaba03fbef"),
        new("sr.caffemodel", "cbfcd60361a73beb8c583eea7e8e6664")
    };

    private static string[]? _cachedModelPaths;
    private static bool _modelDownloadFailed;

    public static Result? TryDecode(Image<Rgba32> image)
    {
        // SmartCar's current capstone deployment is Windows desktop.  Keeping
        // this guard also means Linux CI can build/test without loading the
        // Windows native OpenCV runtime.
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var encoded = new MemoryStream();
            image.SaveAsPng(encoded);
            using var mat = Cv2.ImDecode(encoded.ToArray(), ImreadModes.Color);
            if (mat.Empty())
            {
                return null;
            }

            var text = TryDecodeWithModels(mat) ?? TryDecodeWithoutModels(mat);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return new Result(
                text.Trim(),
                Array.Empty<byte>(),
                Array.Empty<ResultPoint>(),
                BarcodeFormat.QR_CODE);
        }
        catch (Exception ex)
        {
            // A fallback decoder must never break the normal/manual KYC path.
            Debug.WriteLine($"WeChat QR fallback unavailable: {ex.Message}");
            return null;
        }
    }

    private static string? TryDecodeWithModels(Mat image)
    {
        var paths = EnsureModelFiles();
        if (paths is null)
        {
            return null;
        }

        using var detector = new WeChatQRCode(
            paths[0],
            paths[1],
            paths[2],
            paths[3]);

        return DecodeFirst(detector, image);
    }

    private static string? TryDecodeWithoutModels(Mat image)
    {
        // OpenCV still provides its WeChat decoder and cubic scaling when the
        // CNN files are unavailable.  It is weaker than the full model path,
        // but remains a useful offline fallback after ZXing has failed.
        using var detector = new WeChatQRCode();
        return DecodeFirst(detector, image);
    }

    private static string? DecodeFirst(WeChatQRCode detector, Mat image)
    {
        var decoded = detector.DetectAndDecode(image, out _);
        var value = decoded.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        // Retry once on a moderately enlarged image.  This is intentionally
        // bounded because the full WeChat model already performs dedicated
        // detection and super-resolution for small QR codes.
        var longSide = Math.Max(image.Width, image.Height);
        if (longSide >= 1_800)
        {
            return null;
        }

        var scale = Math.Clamp(1_800d / Math.Max(1, longSide), 1.15d, 2.5d);
        using var enlarged = new Mat();
        Cv2.Resize(
            image,
            enlarged,
            new OpenCvSharp.Size(),
            scale,
            scale,
            InterpolationFlags.Cubic);

        decoded = detector.DetectAndDecode(enlarged, out _);
        return decoded.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
    }

    private static string[]? EnsureModelFiles()
    {
        if (_cachedModelPaths is not null)
        {
            return _cachedModelPaths;
        }

        if (_modelDownloadFailed)
        {
            return null;
        }

        lock (ModelSync)
        {
            if (_cachedModelPaths is not null)
            {
                return _cachedModelPaths;
            }

            if (_modelDownloadFailed)
            {
                return null;
            }

            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    localAppData = Path.GetTempPath();
                }

                var modelDirectory = Path.Combine(
                    localAppData,
                    "SmartCar",
                    "Models",
                    "WeChatQr",
                    ModelCommit);
                Directory.CreateDirectory(modelDirectory);

                var paths = new string[ModelSpecs.Length];
                for (var index = 0; index < ModelSpecs.Length; index += 1)
                {
                    var spec = ModelSpecs[index];
                    var path = Path.Combine(modelDirectory, spec.FileName);
                    EnsureOneModel(spec, path);
                    paths[index] = path;
                }

                _cachedModelPaths = paths;
                return paths;
            }
            catch (Exception ex)
            {
                _modelDownloadFailed = true;
                Debug.WriteLine($"Cannot prepare WeChat QR models: {ex.Message}");
                return null;
            }
        }
    }

    private static void EnsureOneModel(ModelSpec spec, string destinationPath)
    {
        if (File.Exists(destinationPath) && HasExpectedMd5(destinationPath, spec.Md5))
        {
            return;
        }

        var bytes = ModelClient
            .GetByteArrayAsync(ModelBaseUrl + spec.FileName)
            .GetAwaiter()
            .GetResult();

        if (!HasExpectedMd5(bytes, spec.Md5))
        {
            throw new InvalidDataException($"Checksum mismatch for {spec.FileName}.");
        }

        var temporaryPath = destinationPath + ".download";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static bool HasExpectedMd5(string path, string expectedMd5)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = MD5.HashData(stream);
            return Convert.ToHexString(hash).Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExpectedMd5(byte[] bytes, string expectedMd5)
    {
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).Equals(expectedMd5, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ModelSpec(string FileName, string Md5);
}
