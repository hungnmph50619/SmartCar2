using Microsoft.AspNetCore.Authorization;
using SmartCar.Domain.Constants;
using SmartCar.Web.Controllers;
using Xunit;

namespace SmartCar.Tests;

public sealed class StaffOperationsAuthorizationTests
{
    [Theory]
    [InlineData(typeof(StaffCustomersController))]
    [InlineData(typeof(HandoversController))]
    [InlineData(typeof(ReturnsController))]
    [InlineData(typeof(ReturnEditsController))]
    [InlineData(typeof(AdminSignedDocumentsController))]
    [InlineData(typeof(AdminRentalDocumentsController))]
    [InlineData(typeof(ReturnHandoverPreviewController))]
    [InlineData(typeof(StaffWorkflowDiagnosticsController))]
    [InlineData(typeof(StaffCounterIdentityEvidenceController))]
    public void OperationalControllers_AreExplicitlyStaffOnly(Type controllerType)
    {
        var authorizeAttributes = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        Assert.NotEmpty(authorizeAttributes);
        Assert.Contains(
            authorizeAttributes,
            attribute => string.Equals(attribute.Roles, RoleNames.Staff, StringComparison.Ordinal));
        Assert.DoesNotContain(
            authorizeAttributes,
            attribute => (attribute.Roles ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(RoleNames.Admin, StringComparer.Ordinal));
    }


    [Fact]
    public void RentalSignedDocumentFilesController_AllowsReadOnlyAccessForAdminAndStaff()
    {
        var authorizeAttributes = typeof(RentalSignedDocumentFilesController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        Assert.NotEmpty(authorizeAttributes);
        var roles = authorizeAttributes
            .SelectMany(attribute => (attribute.Roles ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(RoleNames.Admin, roles);
        Assert.Contains(RoleNames.Staff, roles);
    }
}
