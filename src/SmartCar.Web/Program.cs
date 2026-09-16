using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Filters;
using SmartCar.Web.Services;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ISecureDocumentStorage, SecureDocumentStorage>();
builder.Services.AddScoped<IUserBankAccountService, UserBankAccountService>();
builder.Services.AddHostedService<BookingReservationCleanupService>();
builder.Services.AddScoped<KycAdminNotificationConsolidationFilter>();
builder.Services.AddScoped<AdminWorkNotificationFilter>();
builder.Services.AddScoped<AdminKycFaceDecisionFilter>();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new DuplicateDocumentImagesFilter());
    options.Filters.Add(new KycFaceCaptureFilter());
    options.Filters.AddService<KycAdminNotificationConsolidationFilter>();
    options.Filters.AddService<AdminWorkNotificationFilter>();
    options.Filters.AddService<AdminKycFaceDecisionFilter>();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(
            "/uploads/documents",
            StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();

app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true &&
        context.User.IsInRole(RoleNames.Staff) &&
        !context.Request.Path.StartsWithSegments("/Account/Login", StringComparison.OrdinalIgnoreCase) &&
        !context.Request.Path.StartsWithSegments("/Account/Logout", StringComparison.OrdinalIgnoreCase))
    {
        var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
        var staff = await userManager.GetUserAsync(context.User);

        if (staff is null || !staff.IsActive)
        {
            await signInManager.SignOutAsync();
            context.Response.Redirect("/Account/Login?reason=inactive");
            return;
        }

        if (staff.MustChangePassword &&
            !context.Request.Path.StartsWithSegments("/Account/FirstLoginPassword", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect("/Account/FirstLoginPassword");
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) &&
            !string.IsNullOrWhiteSpace(staff.CitizenIdNumber))
        {
            var dbContext = context.RequestServices.GetRequiredService<ApplicationDbContext>();
            int? bookingId = null;
            string? customerId = null;

            if (context.Request.RouteValues.TryGetValue("bookingId", out var routeBookingId) &&
                int.TryParse(routeBookingId?.ToString(), out var parsedRouteBookingId))
            {
                bookingId = parsedRouteBookingId;
            }

            if (!bookingId.HasValue &&
                int.TryParse(context.Request.Query["bookingId"].FirstOrDefault(), out var parsedQueryBookingId))
            {
                bookingId = parsedQueryBookingId;
            }

            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);

                if (!bookingId.HasValue &&
                    int.TryParse(
                        form["bookingId"].FirstOrDefault() ?? form["BookingId"].FirstOrDefault(),
                        out var parsedFormBookingId))
                {
                    bookingId = parsedFormBookingId;
                }

                customerId = form["customerId"].FirstOrDefault() ?? form["CustomerId"].FirstOrDefault();
            }

            if (bookingId.HasValue && string.IsNullOrWhiteSpace(customerId))
            {
                customerId = await dbContext.Bookings
                    .AsNoTracking()
                    .Where(item => item.BookingId == bookingId.Value)
                    .Select(item => item.CustomerId)
                    .FirstOrDefaultAsync(context.RequestAborted);
            }

            if (!string.IsNullOrWhiteSpace(customerId))
            {
                var isOwnCustomerIdentity = await dbContext.CustomerDocuments
                    .AsNoTracking()
                    .AnyAsync(document =>
                        document.CustomerId == customerId &&
                        (document.DocumentType == DocumentTypes.CitizenId ||
                         document.DocumentType == DocumentTypes.CitizenIdBack) &&
                        document.DocumentNumber == staff.CitizenIdNumber,
                        context.RequestAborted);

                if (isOwnCustomerIdentity)
                {
                    var tempDataFactory = context.RequestServices
                        .GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>();
                    var tempData = tempDataFactory.GetTempData(context);
                    tempData["ErrorMessage"] =
                        "Bạn không được xử lý đơn thuê thuộc tài khoản khách hàng có cùng CCCD với tài khoản nhân viên của mình. Vui lòng chuyển đơn cho nhân viên khác.";

                    try
                    {
                        var auditService = context.RequestServices.GetRequiredService<IAuditService>();
                        await auditService.WriteAsync(
                            staff.Id,
                            "BlockStaffOwnBookingOperation",
                            bookingId.HasValue ? "Booking" : "CustomerAccount",
                            bookingId?.ToString() ?? customerId,
                            bookingId.HasValue
                                ? $"Chặn nhân viên tự thao tác đơn thuê #{bookingId.Value} của chính mình."
                                : "Chặn nhân viên tự tạo đơn tại quầy cho tài khoản Customer của chính mình.",
                            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
                            cancellationToken: context.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        app.Logger.LogWarning(ex, "Không thể ghi Audit Log khi chặn Staff tự xử lý đơn của mình.");
                    }

                    context.Response.Redirect(
                        bookingId.HasValue
                            ? $"/Staff/Details/{bookingId.Value}"
                            : "/Staff/CounterRental");
                    return;
                }
            }
        }
    }

    await next();
});

app.UseAuthorization();

app.Use(async (context, next) =>
{
    await next();

    var isDocumentImageRequest = string.Equals(
        context.Request.Path.Value,
        "/AdminCustomers/ViewDocumentImage",
        StringComparison.OrdinalIgnoreCase);

    if (!isDocumentImageRequest ||
        context.Response.StatusCode != StatusCodes.Status200OK ||
        !context.User.IsInRole(RoleNames.Admin) ||
        !int.TryParse(context.Request.Query["id"], out var documentId))
    {
        return;
    }

    try
    {
        var auditService = context.RequestServices.GetRequiredService<IAuditService>();
        var adminId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        await auditService.WriteAsync(
            adminId,
            "ViewKycDocumentImage",
            "CustomerDocument",
            documentId.ToString(),
            "Quản trị viên xem ảnh CCCD/GPLX để đối chiếu hồ sơ xác minh.",
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: context.RequestAborted);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Không thể ghi Audit Log khi xem ảnh giấy tờ {DocumentId}.", documentId);
    }
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

try
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
    await DatabaseSeeder.SeedAsync(scope.ServiceProvider);
}
catch (Exception ex)
{
    app.Logger.LogWarning(
        ex,
        "Chưa thể cập nhật hoặc seed database. Kiểm tra kết nối SQL Server và migration của SmartCar.");
}

app.Run();
