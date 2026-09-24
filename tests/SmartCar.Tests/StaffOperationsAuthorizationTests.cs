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
    [InlineData(typeof(HandoverEditsController))]
    [InlineData(typeof(ReturnsController))]
    [InlineData(typeof(ReturnEditsController))]
    [InlineData(typeof(AdminSignedDocumentsController))]
    [InlineData(typeof(AdminRentalDocumentsController))]
    [InlineData(typeof(ReturnHandoverPreviewController))]
    [InlineData(typeof(StaffWorkflowDiagnosticsController))]
    [InlineData(typeof(StaffCounterIdentityEvidenceController))]
    [InlineData(typeof(StaffPaymentsController))]
    [InlineData(typeof(StaffExtensionOperationsController))]
    [InlineData(typeof(StaffExtensionRequestsController))]
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
    public void AdminTripRecordsController_IsAdminOnlyReadOnlyHistory()
    {
        var authorizeAttributes = typeof(AdminTripRecordsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        Assert.NotEmpty(authorizeAttributes);
        Assert.Contains(
            authorizeAttributes,
            attribute => string.Equals(attribute.Roles, RoleNames.Admin, StringComparison.Ordinal));

        var declaredActions = typeof(AdminTripRecordsController)
            .GetMethods(System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Details", declaredActions);
        Assert.DoesNotContain(declaredActions, name =>
            name.StartsWith("Upload", StringComparison.Ordinal) ||
            name.StartsWith("Verify", StringComparison.Ordinal) ||
            name.StartsWith("Create", StringComparison.Ordinal) ||
            name.StartsWith("Edit", StringComparison.Ordinal));
    }


    [Fact]
    public void IncomingPaymentReconciliation_BelongsToAdminOnly()
    {
        var publicActions = typeof(AdminPaymentsController)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ConfirmQr", publicActions);
        Assert.Contains("RejectQr", publicActions);

        var staffActions = typeof(StaffPaymentsController)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("ConfirmQr", staffActions);
        Assert.DoesNotContain("RejectQr", staffActions);
    }


    [Fact]
    public void AdminExtensionsController_DoesNotExposeVehicleSwapOperation()
    {
        var publicActions = typeof(AdminExtensionsController)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("MoveConflictingBooking", publicActions);
        Assert.DoesNotContain("CancelConflictingBooking", publicActions);
        Assert.DoesNotContain("CreateForCustomer", publicActions);
    }
}
