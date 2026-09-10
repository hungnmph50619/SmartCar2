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

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IBrandService, BrandService>();

        // Chính sách đặt xe dùng chung cho tìm xe, tạo đơn, giữ chỗ và gia hạn.
        services.AddScoped<BookingReservationPolicy>();
        services.AddScoped<VehicleService>();
        services.AddScoped<IVehicleService, PolicyAwareVehicleService>();

        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IVehicleDocumentService, VehicleDocumentService>();

        services.AddScoped<BookingService>();
        services.AddScoped<IBookingService, PolicyAwareBookingService>();

        services.AddScoped<IBookingOperationService, BookingOperationService>();
        services.AddScoped<ExtensionService>();
        services.AddScoped<IExtensionService, PolicyAwareExtensionService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IHandoverService, HandoverService>();
        services.AddScoped<IReturnService, ReturnService>();
        services.AddScoped<IMaintenanceService, MaintenanceService>();
        services.AddScoped<IncidentService>();
        services.AddScoped<IIncidentService, PolicyAwareIncidentService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IDashboardService, DashboardService>();

        return services;
    }
}
