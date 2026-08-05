using SmartCar.Infrastructure;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<ISecureDocumentStorage, SecureDocumentStorage>();
builder.Services.AddControllersWithViews();

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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

try
{
    using var scope = app.Services.CreateScope();
    await DatabaseSeeder.SeedAsync(scope.ServiceProvider);
}
catch (Exception ex)
{
    app.Logger.LogWarning(
        ex,
        "Chưa thể seed dữ liệu. Hãy tạo migration và cập nhật database theo README_FIRST.txt.");
}

app.Run();
