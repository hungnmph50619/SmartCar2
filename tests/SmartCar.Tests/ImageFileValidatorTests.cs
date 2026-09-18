using Microsoft.AspNetCore.Http;
using SmartCar.Web.Services;
using Xunit;

namespace SmartCar.Tests;

public sealed class ImageFileValidatorTests
{
    [Fact]
    public async Task FindDuplicateContentFieldsAsync_ReturnsEveryImplicatedSemanticField()
    {
        var duplicateBytes = new byte[] { 1, 2, 3, 4, 5 };
        var distinctBytes = new byte[] { 9, 8, 7, 6 };

        var front = CreateFile("front.jpg", duplicateBytes);
        var rear = CreateFile("rear.jpg", duplicateBytes);
        var other = CreateFile("other.jpg", distinctBytes);

        var result = await ImageFileValidator.FindDuplicateContentFieldsAsync(
            new (string FieldName, IFormFile? File)[]
            {
                ("FrontImage", front),
                ("RearImage", rear),
                ("Images", other)
            });

        Assert.Contains("FrontImage", result.Keys);
        Assert.Contains("RearImage", result.Keys);
        Assert.DoesNotContain("Images", result.Keys);
        Assert.Contains("front.jpg", result["FrontImage"]);
        Assert.Contains("rear.jpg", result["FrontImage"]);
        Assert.Contains("front.jpg", result["RearImage"]);
        Assert.Contains("rear.jpg", result["RearImage"]);
    }

    [Fact]
    public async Task FindDuplicateContentFieldsAsync_CollapsesMultipleFilesUnderSameField()
    {
        var duplicateBytes = new byte[] { 5, 4, 3, 2, 1 };

        var result = await ImageFileValidator.FindDuplicateContentFieldsAsync(
            new (string FieldName, IFormFile? File)[]
            {
                ("DamageImages", CreateFile("damage-a.jpg", duplicateBytes)),
                ("DamageImages", CreateFile("damage-b.jpg", duplicateBytes))
            });

        Assert.Single(result);
        Assert.Equal(2, result["DamageImages"].Count);
    }

    private static IFormFile CreateFile(string fileName, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }
}
