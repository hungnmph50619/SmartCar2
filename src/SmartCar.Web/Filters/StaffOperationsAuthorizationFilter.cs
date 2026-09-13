using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartCar.Domain.Constants;

namespace SmartCar.Web.Filters;

/// <summary>
/// Chặn Admin gọi trực tiếp các endpoint vận hành đơn thuê vốn còn mang tên cũ
/// như AdminRentalDocuments/AdminSignedDocuments. Các controller này được giữ tên
/// để tránh phá route/file hiện có, nhưng nghiệp vụ thực tế chỉ Staff được thao tác.
/// </summary>
public sealed class StaffOperationsAuthorizationFilter : IAuthorizationFilter
{
    private static readonly HashSet<string> StaffOnlyControllers = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Staff",
        "StaffCustomers",
        "StaffBookingOperations",
        "Handovers",
        "Returns",
        "AdminRentalDocuments",
        "AdminSignedDocuments",
        "ReturnHandoverPreview"
    };

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
            return;

        var controller = context.RouteData.Values["controller"]?.ToString();
        if (string.IsNullOrWhiteSpace(controller)
            || !StaffOnlyControllers.Contains(controller))
        {
            return;
        }

        if (!user.IsInRole(RoleNames.Staff))
            context.Result = new ForbidResult();
    }
}
