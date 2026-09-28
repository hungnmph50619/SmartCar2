using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;

namespace SmartCar.Web.Filters;

// Check after model binding, before any Staff booking mutation (including file uploads).
public sealed class StaffBookingClaimFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> ProtectedControllers = new(StringComparer.Ordinal)
    {
        "Staff", "StaffBookingOperations", "StaffPayments", "StaffExtensionRequests",
        "StaffCounterIdentityEvidence", "Handovers", "HandoverEdits", "Returns",
        "ReturnEdits", "AdminRentalDocuments"
    };

    private readonly StaffBookingClaimService _claims;

    public StaffBookingClaimFilter(StaffBookingClaimService claims) => _claims = claims;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method) ||
            !context.HttpContext.User.IsInRole(RoleNames.Staff) ||
            context.ActionDescriptor is not ControllerActionDescriptor descriptor)
        {
            await next();
            return;
        }

        var isIdentityCapture = descriptor.ControllerName == "IdentityCapture" &&
            (descriptor.ActionName is "CreateSession" or "StaffFallback");
        var isProtected = ProtectedControllers.Contains(descriptor.ControllerName) || isIdentityCapture;
        var excluded = descriptor.ControllerName == "Staff" &&
            (descriptor.ActionName is "CounterRental" or "ClaimBooking" or "ReleaseBooking");
        if (!isProtected || excluded)
        {
            await next();
            return;
        }

        var bookingId = FindBookingId(context.ActionArguments);
        // Customer KYC captures have no booking; they do not touch a rental order.
        if (bookingId is null && isIdentityCapture)
        {
            await next();
            return;
        }

        if (bookingId is null ||
            !await _claims.TryRenewAsync(bookingId.Value,
                context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
                context.HttpContext.RequestAborted))
        {
            const string message = "Đơn chưa được bạn nhận xử lý hoặc lượt nhận đã hết hạn. " +
                "Hãy mở chi tiết đơn và bấm Nhận xử lý trước khi lưu.";
            var ajax = isIdentityCapture ||
                descriptor.ControllerName == "StaffCounterIdentityEvidence" ||
                context.HttpContext.Request.Headers["Accept"].ToString().Contains("json", StringComparison.OrdinalIgnoreCase) ||
                context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest";
            if (ajax || context.Controller is not Controller controller)
            {
                context.Result = new ConflictObjectResult(new { error = message });
            }
            else
            {
                controller.TempData["ErrorMessage"] = message;
                context.Result = new RedirectToActionResult("Details", "Staff", new { id = bookingId });
            }
            return;
        }

        await next();
    }

    private static int? FindBookingId(IDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("bookingId", out var direct))
        {
            if (direct is int id && id > 0) return id;
        }

        foreach (var value in arguments.Values)
        {
            var property = value?.GetType().GetProperty("BookingId", BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(value) is int id && id > 0) return id;
        }
        return null;
    }
}
