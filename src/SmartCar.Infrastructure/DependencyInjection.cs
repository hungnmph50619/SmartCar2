using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartCar.Application.Features.Accounts;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Brands;
using SmartCar.Application.Features.Dashboard;
using SmartCar.Application.Features.Handovers;
using SmartCar.Application.Features.Payments;
using SmartCar.Application.Features.Returns;
using SmartCar.Application.Features.Vehicles;
using SmartCar.Infrastructure.Identity;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Infrastructure.Services;

namespace SmartCar.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Không tìm thấy ConnectionStrings:DefaultConnection.");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString));

        services
            .AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.User.RequireUniqueEmail = true;

                options.Password.RequiredLength = 6;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
        });

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IBrandService, BrandService>();
        services.AddScoped<IVehicleService, VehicleService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IHandoverService, HandoverService>();
        services.AddScoped<IReturnService, ReturnService>();
        services.AddScoped<IDashboardService, DashboardService>();

        return services;
    }
}
