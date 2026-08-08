using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SmartCar.Web.Filters;

/// <summary>
/// KYC quality gate shared by automatic reading and the final manual submit.
/// QR/MRZ/OCR is never allowed to turn a poor document photo into an accepted one.
/// </summary>
public sealed class StrictDocumentImageQualityFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> ProtectedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "PreviewCitizenQrMrz",
        "PreviewLicenseQr",
        "VerifyAndSubmitCitizenId",
        "SubmitCitizenId",
        "SubmitDrivingLicense"
    };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var action = context.ActionDescriptor.RouteValues.TryGetValue("action", out var actionName)
            ? actionName
            : null;
        if (string.IsNullOrWhiteSpace(action) || !ProtectedActions.Contains(action) ||
            !context.HttpContext.Request.HasFormContentType)
        {
            await next();
            return;
        }

        var cancellationToken = context.HttpContext.RequestAborted;
        var form = await context.HttpContext.Request.ReadFormAsync(cancellationToken);
        var images = form.Files
            .Where(file => file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.Name.Contains("front", StringComparison.OrdinalIgnoreCase) ||
                           file.Name.Contains("back", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (images.Count == 0)
        {
            await next();
            return;
        }

        var errors = new List<(IFormFile File, IReadOnlyList<string> Failures)>();
        foreach (var file in images)
        {
            var result = await DocumentImageQualityValidator.ValidateAsync(file, cancellationToken);
            if (!result.Passed)
            {
                errors.Add((file, result.Failures));
            }
        }

        if (errors.Count == 0)
        {
            await next();
            return;
        }

        var isProfileSubmit = action.Equals("SubmitCitizenId", StringComparison.OrdinalIgnoreCase) ||
                              action.Equals("SubmitDrivingLicense", StringComparison.OrdinalIgnoreCase);
        if (isProfileSubmit)
        {
            foreach (var (file, failures) in errors)
            {
                var isBack = file.Name.Contains("back", StringComparison.OrdinalIgnoreCase);
                var prefix = action.Equals("SubmitCitizenId", StringComparison.OrdinalIgnoreCase)
                    ? "CitizenIdVerification"
                    : "DrivingLicenseVerification";
                var key = $"{prefix}.{(isBack ? "BackImage" : "FrontImage")}";
                foreach (var failure in failures)
                {
                    context.ModelState.AddModelError(key, failure);
                }
            }

            // ProfileController already renders the KYC page when ModelState is invalid.
            await next();
            return;
        }

        var messages = errors.SelectMany(item =>
        {
            var side = item.File.Name.Contains("back", StringComparison.OrdinalIgnoreCase)
                ? "Mặt sau"
                : "Mặt trước";
            return item.Failures.Select(failure => $"{side}: {failure}");
        }).Distinct().ToArray();

        context.Result = new BadRequestObjectResult(new
        {
            succeeded = false,
            code = "IMAGE_QUALITY_FAILED",
            errors = messages
        });
    }
}

internal static class DocumentImageQualityValidator
{
    private const int MinCardShortSide = 300;
    private const int MinCardLongSide = 500;
    private const double MinDocumentAreaRatio = 0.12;
    private const double MinSharpness = 45;
    private const double MinBrightness = 42;
    private const double MaxBrightness = 228;
    private const double MaxDarkRatio = 0.62;
    private const double MaxBrightRatio = 0.40;
    private const double MinEdgeDensity = 0.009;

    public static async Task<QualityResult> ValidateAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);
            image.Mutate(context => context.AutoOrient());

            var sourceWidth = image.Width;
            var sourceHeight = image.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return Fail("Không đọc được kích thước ảnh. Hãy chọn lại ảnh khác.");
            }

            var frameAspect = Math.Max(sourceWidth, sourceHeight) /
                              (double)Math.Max(1, Math.Min(sourceWidth, sourceHeight));

            DocumentBounds? bounds = null;
            var fullFrame = frameAspect is >= 1.38 and <= 1.82;
            if (!fullFrame)
            {
                bounds = DetectDocumentBounds(image);
                if (bounds is null)
                {
                    return Fail("Không nhận ra trọn vùng giấy tờ. Hãy để đủ 4 góc, chụp gần hơn và dùng nền tương phản với thẻ.");
                }
            }

            var areaRatio = fullFrame ? 1d : bounds!.AreaRatio;
            var cardWidth = fullFrame ? sourceWidth : bounds!.Width;
            var cardHeight = fullFrame ? sourceHeight : bounds!.Height;
            var cardShort = Math.Min(cardWidth, cardHeight);
            var cardLong = Math.Max(cardWidth, cardHeight);

            var failures = new List<string>();
            if (cardShort < MinCardShortSide || cardLong < MinCardLongSide)
            {
                failures.Add("Giấy tờ ở quá xa nên chữ và mã QR/MRZ quá nhỏ. Hãy chụp gần hơn.");
            }
            if (!fullFrame && areaRatio < MinDocumentAreaRatio)
            {
                failures.Add("Giấy tờ chiếm quá ít khung hình. Hãy đưa giấy tờ gần camera hơn.");
            }

            using var document = fullFrame
                ? image.Clone()
                : image.Clone(context => context.Crop(ToRectangle(bounds!, sourceWidth, sourceHeight)));
            using var sample = ResizeForAnalysis(document, 760);
            var metrics = Analyze(sample);

            if (metrics.Sharpness < MinSharpness)
            {
                failures.Add("Ảnh bị mờ hoặc rung. Hãy giữ máy ổn định và chụp lại.");
            }
            if (metrics.Brightness < MinBrightness || metrics.DarkRatio > MaxDarkRatio)
            {
                failures.Add("Ảnh quá tối. Hãy chụp ở nơi đủ sáng.");
            }
            if (metrics.Brightness > MaxBrightness || metrics.BrightRatio > MaxBrightRatio)
            {
                failures.Add("Ảnh bị lóa hoặc cháy sáng. Hãy đổi góc chụp để thông tin không bị che.");
            }
            if (metrics.EdgeDensity < MinEdgeDensity)
            {
                failures.Add("Chữ trên giấy tờ chưa đủ rõ. Hãy chụp gần hơn và lấy nét vào giấy tờ.");
            }

            return new QualityResult(failures.Count == 0, failures.Distinct().ToArray());
        }
        catch
        {
            return Fail("Không thể kiểm tra chất lượng ảnh. Hãy chọn lại ảnh JPG, PNG hoặc WEBP hợp lệ.");
        }
    }

    private static QualityResult Fail(string message) => new(false, new[] { message });

    private static Image<Rgba32> ResizeForAnalysis(Image<Rgba32> source, int maxSide)
    {
        var scale = Math.Min(1d, maxSide / (double)Math.Max(source.Width, source.Height));
        return source.Clone(context => context.Resize(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale))));
    }

    private static Rectangle ToRectangle(DocumentBounds bounds, int width, int height)
    {
        var x = Math.Clamp((int)Math.Floor(bounds.X), 0, Math.Max(0, width - 1));
        var y = Math.Clamp((int)Math.Floor(bounds.Y), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling(bounds.X + bounds.Width), x + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Y + bounds.Height), y + 1, height);
        return new Rectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static DocumentBounds? DetectDocumentBounds(Image<Rgba32> source)
    {
        using var sample = ResizeForAnalysis(source, 720);
        var borderR = new List<byte>();
        var borderG = new List<byte>();
        var borderB = new List<byte>();
        var step = Math.Max(2, Math.Min(sample.Width, sample.Height) / 80);

        sample.ProcessPixelRows(accessor =>
        {
            for (var x = 0; x < sample.Width; x += step)
            {
                Add(accessor.GetRowSpan(0)[x]);
                Add(accessor.GetRowSpan(sample.Height - 1)[x]);
            }
            for (var y = 0; y < sample.Height; y += step)
            {
                Add(accessor.GetRowSpan(y)[0]);
                Add(accessor.GetRowSpan(y)[sample.Width - 1]);
            }
        });

        void Add(Rgba32 pixel)
        {
            borderR.Add(pixel.R);
            borderG.Add(pixel.G);
            borderB.Add(pixel.B);
        }

        var bgR = Median(borderR);
        var bgG = Median(borderG);
        var bgB = Median(borderB);
        var xs = new List<int>();
        var ys = new List<int>();

        sample.ProcessPixelRows(accessor =>
        {
            for (var y = 1; y < sample.Height - 1; y += 2)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 1; x < sample.Width - 1; x += 2)
                {
                    var pixel = row[x];
                    var dr = pixel.R - bgR;
                    var dg = pixel.G - bgG;
                    var db = pixel.B - bgB;
                    var distance = Math.Sqrt(dr * dr + dg * dg + db * db);
                    var cyanLike = pixel.B > pixel.R * 0.95 &&
                                   pixel.G > pixel.R * 0.95 &&
                                   pixel.G + pixel.B - 2 * pixel.R > 18;
                    var lightCard = pixel.R > 145 && pixel.G > 155 && pixel.B > 150 && distance > 28;
                    if (distance > 68 || cyanLike || lightCard)
                    {
                        xs.Add(x);
                        ys.Add(y);
                    }
                }
            }
        });

        if (xs.Count < sample.Width * sample.Height / 180)
        {
            return null;
        }

        xs.Sort();
        ys.Sort();
        var left = Percentile(xs, 0.025);
        var right = Percentile(xs, 0.975);
        var top = Percentile(ys, 0.025);
        var bottom = Percentile(ys, 0.975);
        var detectedWidth = right - left;
        var detectedHeight = bottom - top;
        if (detectedWidth < 80 || detectedHeight < 45)
        {
            return null;
        }

        var padX = detectedWidth * 0.035;
        var padY = detectedHeight * 0.055;
        left = Math.Max(0, left - padX);
        right = Math.Min(sample.Width - 1, right + padX);
        top = Math.Max(0, top - padY);
        bottom = Math.Min(sample.Height - 1, bottom + padY);

        var width = right - left;
        var height = bottom - top;
        var aspect = Math.Max(width, height) / Math.Max(1d, Math.Min(width, height));
        if (aspect < 1.20 || aspect > 2.10)
        {
            return null;
        }

        var scaleX = source.Width / (double)sample.Width;
        var scaleY = source.Height / (double)sample.Height;
        return new DocumentBounds(
            left * scaleX,
            top * scaleY,
            width * scaleX,
            height * scaleY,
            width * height / (sample.Width * (double)sample.Height));
    }

    private static AnalysisMetrics Analyze(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var gray = new float[width * height];
        double brightnessSum = 0;
        var darkCount = 0;
        var brightCount = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var pixel = row[x];
                    var lum = (float)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B);
                    gray[y * width + x] = lum;
                    brightnessSum += lum;
                    if (lum < 45) darkCount++;
                    if (lum > 245) brightCount++;
                }
            }
        });

        double lapSum = 0;
        double lapSq = 0;
        var lapCount = 0;
        var edgeCount = 0;
        var edgeTotal = 0;
        for (var y = 1; y < height - 1; y += 2)
        {
            for (var x = 1; x < width - 1; x += 2)
            {
                var center = gray[y * width + x];
                var lap = gray[(y - 1) * width + x] + gray[(y + 1) * width + x] +
                          gray[y * width + x - 1] + gray[y * width + x + 1] - 4 * center;
                lapSum += lap;
                lapSq += lap * lap;
                lapCount++;

                var gx = gray[y * width + x + 1] - gray[y * width + x - 1];
                var gy = gray[(y + 1) * width + x] - gray[(y - 1) * width + x];
                if (Math.Sqrt(gx * gx + gy * gy) > 35) edgeCount++;
                edgeTotal++;
            }
        }

        var pixelCount = Math.Max(1, width * height);
        var brightness = brightnessSum / pixelCount;
        var darkRatio = darkCount / (double)pixelCount;
        var brightRatio = brightCount / (double)pixelCount;
        var lapMean = lapCount > 0 ? lapSum / lapCount : 0;
        var sharpness = lapCount > 0 ? Math.Max(0, lapSq / lapCount - lapMean * lapMean) : 0;
        var edgeDensity = edgeTotal > 0 ? edgeCount / (double)edgeTotal : 0;
        return new AnalysisMetrics(brightness, darkRatio, brightRatio, sharpness, edgeDensity);
    }

    private static double Median(List<byte> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    private static double Percentile(List<int> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Floor((values.Count - 1) * percentile), 0, values.Count - 1);
        return values[index];
    }

    internal sealed record QualityResult(bool Passed, IReadOnlyList<string> Failures);
    private sealed record DocumentBounds(double X, double Y, double Width, double Height, double AreaRatio);
    private sealed record AnalysisMetrics(
        double Brightness,
        double DarkRatio,
        double BrightRatio,
        double Sharpness,
        double EdgeDensity);
}
