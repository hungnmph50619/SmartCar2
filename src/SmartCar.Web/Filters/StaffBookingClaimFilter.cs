using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartCar.Domain.Constants;
using SmartCar.Web.Services;

namespace SmartCar.Web.Filters;

public sealed class StaffBookingClaimFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> ProtectedControllers =
        new(StringComparer.Ordinal)
        {
            "Staff",
            "StaffBookingOperations",
            "StaffPayments",
            "StaffExtensionRequests",
            "StaffCounterIdentityEvidence",
            "Handovers",
            "HandoverEdits",
            "Returns",
            "ReturnEdits",
            "AdminRentalDocuments"
        };

    private readonly StaffBookingClaimService _claims;

    public StaffBookingClaimFilter(StaffBookingClaimService claims) =>
        _claims = claims;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method) ||
            !context.HttpContext.User.IsInRole(RoleNames.Staff) ||
            context.ActionDescriptor is not ControllerActionDescriptor descriptor)
        {
            await next();
            return;
        }

        var isIdentityCapture =
            descriptor.ControllerName == "IdentityCapture" &&
            descriptor.ActionName is "CreateSession" or "StaffFallback";

        var isProtected =
            ProtectedControllers.Contains(descriptor.ControllerName) ||
            isIdentityCapture;

        var excluded =
            descriptor.ControllerName == "Staff" &&
            descriptor.ActionName is "CounterRental" or "ClaimBooking" or "ReleaseBooking";

        if (!isProtected || excluded)
        {
            await next();
            return;
        }

        var bookingId = FindBookingId(context.ActionArguments);

        if (bookingId is null &&
            context.ActionArguments.TryGetValue("paymentId", out var paymentValue) &&
            paymentValue is int paymentId &&
            paymentId > 0)
        {
            bookingId = await _claims.ResolveBookingIdByPaymentAsync(
                paymentId,
                context.HttpContext.RequestAborted);
        }

        if (bookingId is null && isIdentityCapture)
        {
            await next();
            return;
        }

        var staffId =
            context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            string.Empty;

        if (bookingId is null ||
            !await _claims.TryRenewAsync(
                bookingId.Value,
                staffId,
                context.HttpContext.RequestAborted))
        {
            const string message =
                "Đơn chưa được bạn nhận xử lý hoặc quyền xử lý đã hết hạn. " +
                "Hãy mở chi tiết đơn và bấm Nhận xử lý trước khi lưu.";

            var ajax =
                isIdentityCapture ||
                descriptor.ControllerName == "StaffCounterIdentityEvidence" ||
                context.HttpContext.Request.Headers["Accept"]
                    .ToString()
                    .Contains("json", StringComparison.OrdinalIgnoreCase) ||
                context.HttpContext.Request.Headers["X-Requested-With"] ==
                    "XMLHttpRequest";

            if (ajax || context.Controller is not Controller controller)
            {
                context.Result =
                    new ConflictObjectResult(new { error = message });
            }
            else
            {
                controller.TempData["ErrorMessage"] = message;
                context.Result = new RedirectToActionResult(
                    "Details",
                    "Staff",
                    new { id = bookingId });
            }

            return;
        }

        await next();
    }

    private static int? FindBookingId(
        IDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("bookingId", out var direct) &&
            direct is int directId &&
            directId > 0)
        {
            return directId;
        }

        foreach (var value in arguments.Values)
        {
            var property = value?
                .GetType()
                .GetProperty(
                    "BookingId",
                    BindingFlags.Public | BindingFlags.Instance);

            if (property?.GetValue(value) is int id && id > 0)
            {
                return id;
            }
        }

        return null;
    }
}
