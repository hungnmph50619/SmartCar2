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
    public void RentalSignedDocumentFilesController_AllowsReadOnlyAccessForAdminStaffAndCustomer()
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
        Assert.Contains(RoleNames.Customer, roles);
    }

    [Fact]
    public void AdminRentalDocuments_ReadsAllowAdminButUploadsRemainStaffOnly()
    {
        var controllerRoles = typeof(AdminRentalDocumentsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SelectMany(attribute => (attribute.Roles ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(RoleNames.Admin, controllerRoles);
        Assert.Contains(RoleNames.Staff, controllerRoles);

        foreach (var methodName in new[]
                 {
                     nameof(AdminRentalDocumentsController.UploadHandoverSigned),
                     nameof(AdminRentalDocumentsController.UploadReturnSigned)
                 })
        {
            var method = typeof(AdminRentalDocumentsController).GetMethod(methodName);
            Assert.NotNull(method);

            var methodRoles = method!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .SelectMany(attribute => (attribute.Roles ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains(RoleNames.Staff, methodRoles);
            Assert.DoesNotContain(RoleNames.Admin, methodRoles);
        }
    }

}
