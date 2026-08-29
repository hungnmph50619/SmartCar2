using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Domain.Constants;
using SmartCar.Infrastructure;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Filters;
using SmartCar.Web.Services;

// Giữ tiếng Việt hiển thị đúng trong Developer PowerShell/Terminal khi chạy app.
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ISecureDocumentStorage, SecureDocumentStorage>();
builder.Services.AddScoped<IUserBankAccountService, UserBankAccountService>();
builder.Services.AddScoped<KycAdminNotificationConsolidationFilter>();
builder.Services.AddScoped<AdminWorkNotificationFilter>();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new DuplicateDocumentImagesFilter());
    options.Filters.AddService<KycAdminNotificationConsolidationFilter>();
    options.Filters.AddService<AdminWorkNotificationFilter>();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Không cho truy cập trực tiếp ảnh CCCD/GPLX cũ trong wwwroot.
// Ảnh chỉ được trả về thông qua controller sau khi kiểm tra quyền.
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
app.UseAuthorization();

// Ảnh CCCD/GPLX là dữ liệu nhạy cảm. Khi Quản trị viên xem ảnh thông qua
// endpoint có phân quyền, ghi lại thao tác để có thể truy vết khi cần.
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
