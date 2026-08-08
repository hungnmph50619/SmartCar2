using SixLabors.ImageSharp.PixelFormats;

namespace SmartCar.Web.Controllers;

/// <summary>
/// Local adapter so CitizenQrController resolves BarcodeReader<TPixel>
/// to the ImageSharp binding instead of the generic ZXing base reader.
/// </summary>
internal sealed class BarcodeReader<TPixel> : ZXing.ImageSharp.BarcodeReader<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
}
