using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartCar.Application.Features.Accounts;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Bookings;
using SmartCar.Application.Features.Brands;
using SmartCar.Application.Features.Dashboard;
using SmartCar.Application.Features.Documents;
using SmartCar.Application.Features.Extensions;
using SmartCar.Application.Features.Handovers;
using SmartCar.Application.Features.Incidents;
using SmartCar.Application.Features.Maintenance;
using SmartCar.Application.Features.Notifications;
using SmartCar.Application.Features.Operations;
using SmartCar.Application.Features.Payments;
using SmartCar.Application.Features.Reports;
using SmartCar.Application.Features.Returns;
using SmartCar.Application.Features.Reviews;
using SmartCar.Application.Features.VehicleDocuments;
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

                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = true;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddErrorDescriber<VietnameseIdentityErrorDescriber>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<DataProtectionTokenProviderOptions>(options =>
        {
            options.TokenLifespan = TimeSpan.FromMinutes(30);
        });

        services.Configure<SmtpEmailOptions>(
            configuration.GetSection(SmtpEmailOptions.SectionName));

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
        });

        services.AddMemoryCache();
        services.AddHttpClient<IDeliveryQuoteService, DeliveryQuoteService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartCarStudentDemo/1.0");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9,en;q=0.7");
        });

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IBrandService, BrandService>();
        services.AddScoped<IVehicleService, VehicleService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IVehicleDocumentService, VehicleDocumentService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IBookingOperationService, BookingOperationService>();
        services.AddScoped<IExtensionService, ExtensionService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IHandoverService, HandoverService>();
        services.AddScoped<IReturnService, ReturnService>();
        services.AddScoped<IMaintenanceService, MaintenanceService>();
        services.AddScoped<IIncidentService, IncidentService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IDashboardService, DashboardService>();

        return services;
    }
}
