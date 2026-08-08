using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ZXing;

namespace SmartCar.Web.Controllers;

/// <summary>
/// Local adapter so CitizenQrController resolves BarcodeReader&lt;TPixel&gt;
/// to the ImageSharp binding instead of the generic ZXing base reader.
///
/// CCCD photos are often taken from farther away, so the QR can look clear to a
/// person while each QR module only has a few source pixels. The adapter keeps
/// ZXing as the fast decoder, adds bounded local preprocessing, then invokes the
/// heavier WeChat QR detector/super-resolution fallback at most once per reader.
/// </summary>
internal sealed class BarcodeReader<TPixel> : ZXing.ImageSharp.BarcodeReader<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private const long MaximumTightScanPixels = 1_400_000;
    private bool _weChatFallbackAttempted;

    public new Result? Decode(Image<TPixel> image)
    {
        var direct = base.Decode(image);
        if (HasText(direct))
        {
            return direct;
        }

        using var rgba = image.CloneAs<Rgba32>();
        var rgbaReader = new ZXing.ImageSharp.BarcodeReader<Rgba32>
        {
            AutoRotate = AutoRotate,
            TryInverted = TryInverted,
            Options = Options
        };

        var enhanced = TryDecodeVariants(rgbaReader, rgba);
        if (HasText(enhanced))
        {
            return enhanced;
        }

        // Only do the tighter sliding-window scan on the original-sized image.
        // CitizenQrController already retries larger 62% tiles afterwards; doing
        // another nested scan on those enlarged tiles would waste CPU.
        if ((long)rgba.Width * rgba.Height <= MaximumTightScanPixels)
        {
            foreach (var tile in BuildQrCandidateTiles(rgba.Width, rgba.Height))
            {
                using var cropped = rgba.Clone(context => context.Crop(tile));
                var decoded = TryDecodeVariants(rgbaReader, cropped);
                if (HasText(decoded))
                {
                    return decoded;
                }
            }
        }

        // The controller reuses one reader while trying several large tiles.
        // Run the expensive OpenCV fallback only once so a failed QR does not
        // trigger repeated CNN inference/model loading for every tile.
        if (!_weChatFallbackAttempted)
        {
            _weChatFallbackAttempted = true;
            var weChatDecoded = WeChatQrFallbackDecoder.TryDecode(rgba);
            if (HasText(weChatDecoded))
            {
                return weChatDecoded;
            }
        }

        return null;
    }

    private static Result? TryDecodeVariants(
        ZXing.ImageSharp.BarcodeReader<Rgba32> reader,
        Image<Rgba32> source)
    {
        var longSide = Math.Max(source.Width, source.Height);
        var scale = longSide < 1_200
            ? Math.Clamp(1_200d / Math.Max(1, longSide), 1d, 5.5d)
            : 1d;

        using var working = source.Clone(context =>
        {
            if (scale > 1.05d)
            {
                context.Resize(
                    Math.Max(1, (int)Math.Round(source.Width * scale)),
                    Math.Max(1, (int)Math.Round(source.Height * scale)));
            }
        });

        var decoded = reader.Decode(working);
        if (HasText(decoded))
        {
            return decoded;
        }

        using (var contrast = working.Clone())
        {
            ApplyGrayscaleContrast(contrast, 1.55d);
            decoded = reader.Decode(contrast);
            if (HasText(decoded))
            {
                return decoded;
            }
        }

        // Different phones/exposures place the useful black/white split at
        // different luminance levels. A few fixed thresholds are cheap and
        // substantially more robust than relying on one global binarization.
        foreach (var threshold in new[] { 105, 140, 175 })
        {
            using var binary = working.Clone();
            ApplyBinaryThreshold(binary, threshold);
            decoded = reader.Decode(binary);
            if (HasText(decoded))
            {
                return decoded;
            }
        }

        return null;
    }

    private static IEnumerable<Rectangle> BuildQrCandidateTiles(int width, int height)
    {
        var seen = new HashSet<(int X, int Y, int Width, int Height)>();
        var plans = new[]
        {
            (Ratio: 0.48d, Positions: 3),
            (Ratio: 0.32d, Positions: 4)
        };

        foreach (var plan in plans)
        {
            var tileWidth = Math.Clamp((int)Math.Round(width * plan.Ratio), 96, width);
            var tileHeight = Math.Clamp((int)Math.Round(height * plan.Ratio), 96, height);
            var maxX = Math.Max(0, width - tileWidth);
            var maxY = Math.Max(0, height - tileHeight);

            for (var row = 0; row < plan.Positions; row += 1)
            {
                var y = plan.Positions == 1
                    ? 0
                    : (int)Math.Round(maxY * row / (double)(plan.Positions - 1));

                for (var column = 0; column < plan.Positions; column += 1)
                {
                    var x = plan.Positions == 1
                        ? 0
                        : (int)Math.Round(maxX * column / (double)(plan.Positions - 1));

                    var key = (x, y, tileWidth, tileHeight);
                    if (seen.Add(key))
                    {
                        yield return new Rectangle(x, y, tileWidth, tileHeight);
                    }
                }
            }
        }
    }

    private static void ApplyGrayscaleContrast(Image<Rgba32> image, double contrast)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y += 1)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += 1)
                {
                    var pixel = row[x];
                    var gray = ToLuminance(pixel);
                    var adjusted = (int)Math.Round((gray - 128) * contrast + 128);
                    var value = (byte)Math.Clamp(adjusted, 0, 255);
                    row[x] = new Rgba32(value, value, value, pixel.A);
                }
            }
        });
    }

    private static void ApplyBinaryThreshold(Image<Rgba32> image, int threshold)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y += 1)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x += 1)
                {
                    var pixel = row[x];
                    var value = ToLuminance(pixel) >= threshold ? (byte)255 : (byte)0;
                    row[x] = new Rgba32(value, value, value, pixel.A);
                }
            }
        });
    }

    private static int ToLuminance(Rgba32 pixel) =>
        (int)Math.Round(pixel.R * 0.299d + pixel.G * 0.587d + pixel.B * 0.114d);

    private static bool HasText(Result? result) =>
        !string.IsNullOrWhiteSpace(result?.Text);
}
