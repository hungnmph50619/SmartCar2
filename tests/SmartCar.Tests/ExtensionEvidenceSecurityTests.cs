using Microsoft.AspNetCore.Authorization;
using SmartCar.Domain.Constants;
using SmartCar.Web.Controllers;
using SmartCar.Web.Services;
using Xunit;

namespace SmartCar.Tests;

public sealed class ExtensionEvidenceSecurityTests
{
    [Fact]
    public void ExtractImagePaths_ReturnsOnlySupportedStoredEvidencePaths()
    {
        const string note =
            "[FORCE_MAJEURE]\n" +
            "[EVIDENCE]Xe thủng lốp | Vị trí trực tiếp: 21.0, 105.8 | " +
            "Ảnh minh chứng: secure-documents/extension-evidence-booking-10/a.jpg; " +
            "/uploads/extensions/10/b.png; https://example.com/not-allowed.jpg\n" +
            "[NOTE]Cần thêm thời gian";

        var result = ExtensionEvidencePathParser.ExtractImagePaths(note);

        Assert.Equal(2, result.Count);
        Assert.Equal(
            "secure-documents/extension-evidence-booking-10/a.jpg",
            result[0]);
        Assert.Equal(
            "/uploads/extensions/10/b.png",
            result[1]);
    }

    [Fact]
    public void ExtractImagePaths_WithoutImageMarker_ReturnsEmpty()
    {
        const string note =
            "[FORCE_MAJEURE]\n" +
            "[EVIDENCE]Vị trí trực tiếp: 21.0, 105.8\n" +
            "[NOTE]Cần thêm thời gian";

        Assert.Empty(
            ExtensionEvidencePathParser.ExtractImagePaths(note));
    }

    [Fact]
    public void EvidenceFileController_IsRestrictedToAdminAndCustomer()
    {
        var authorize = typeof(ExtensionEvidenceFilesController)
            .GetCustomAttributes(
                typeof(AuthorizeAttribute),
                inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        Assert.NotEmpty(authorize);

        var roles = authorize
            .SelectMany(attribute =>
                (attribute.Roles ?? string.Empty).Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(RoleNames.Admin, roles);
        Assert.Contains(RoleNames.Customer, roles);
        Assert.DoesNotContain(RoleNames.Staff, roles);
    }
}
