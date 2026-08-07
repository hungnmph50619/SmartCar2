using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Identity;

namespace SmartCar.Infrastructure.Persistence;

public static class DatabaseSeeder
{
    private const string AdminEmail = "admin@smartcar.vn";
    private const string DemoCustomerEmail = "customer@smartcar.vn";
    private const string DemoPassword = "SmartCar@123";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();

        foreach (var roleName in new[] { RoleNames.Customer, RoleNames.Admin })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(roleName));
                EnsureSucceeded(roleResult, $"Không thể tạo vai trò {roleName}");
            }
        }

        var admin = await EnsureUserAsync(
            userManager,
            AdminEmail,
            "Quản trị SmartCar",
            "0900000000",
            RoleNames.Admin);

        var demoCustomer = await EnsureUserAsync(
            userManager,
            DemoCustomerEmail,
            "Khách hàng Demo",
            "0911111111",
            RoleNames.Customer);

        if (!await dbContext.Brands.AnyAsync())
        {
            dbContext.Brands.AddRange(
                new Brand { BrandName = "Toyota" },
                new Brand { BrandName = "Honda" },
                new Brand { BrandName = "Hyundai" },
                new Brand { BrandName = "Kia" },
                new Brand { BrandName = "Ford" },
                new Brand { BrandName = "Mazda" });

            await dbContext.SaveChangesAsync();
        }

        if (!await dbContext.Vehicles.AnyAsync())
        {
            var brands = await dbContext.Brands
                .ToDictionaryAsync(item => item.BrandName, StringComparer.OrdinalIgnoreCase);

            dbContext.Vehicles.AddRange(
                CreateVehicle(brands["Toyota"].BrandId, "Toyota Vios 1.5G", "Vios", "30A-123.45", 2024, 5, "Tự động", "Xăng", "Trắng", 750_000m, 12_500, "/images/demo/toyota-vios.svg"),
                CreateVehicle(brands["Honda"].BrandId, "Honda City RS", "City", "30A-234.56", 2024, 5, "Tự động", "Xăng", "Đỏ", 850_000m, 10_200, "/images/demo/honda-city.svg"),
                CreateVehicle(brands["Hyundai"].BrandId, "Hyundai Accent", "Accent", "30A-345.67", 2023, 5, "Tự động", "Xăng", "Bạc", 720_000m, 22_300, "/images/demo/hyundai-accent.svg"),
                CreateVehicle(brands["Kia"].BrandId, "Kia Seltos Luxury", "Seltos", "30A-456.78", 2024, 5, "Tự động", "Xăng", "Cam", 1_050_000m, 8_900, "/images/demo/kia-seltos.svg"),
                CreateVehicle(brands["Mazda"].BrandId, "Mazda CX-5 Premium", "CX-5", "30A-567.89", 2023, 5, "Tự động", "Xăng", "Đỏ", 1_250_000m, 18_400, "/images/demo/mazda-cx5.svg"),
                CreateVehicle(brands["Ford"].BrandId, "Ford Everest Titanium", "Everest", "30A-678.90", 2024, 7, "Tự động", "Dầu", "Xanh", 1_650_000m, 15_600, "/images/demo/ford-everest.svg"));

            await dbContext.SaveChangesAsync();
        }

        if (!await dbContext.CustomerDocuments.AnyAsync(item => item.CustomerId == demoCustomer.Id))
        {
            var now = DateTime.UtcNow;
            dbContext.CustomerDocuments.AddRange(
                new CustomerDocument
                {
                    CustomerId = demoCustomer.Id,
                    DocumentType = DocumentTypes.CitizenIdentityCard,
                    DocumentNumber = "001204000001",
                    ExpiryDate = DateTime.Today.AddYears(10),
                    ImagePath = "/images/demo/document-placeholder.svg",
                    Status = DocumentStatus.Verified,
                    VerifiedBy = admin.Id,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new CustomerDocument
                {
                    CustomerId = demoCustomer.Id,
                    DocumentType = DocumentTypes.DrivingLicense,
                    DocumentNumber = "790000000001",
                    ExpiryDate = DateTime.Today.AddYears(5),
                    ImagePath = "/images/demo/document-placeholder.svg",
                    Status = DocumentStatus.Verified,
                    VerifiedBy = admin.Id,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });

            await dbContext.SaveChangesAsync();
        }

        await EnsureDemoDocumentAsync(
            dbContext,
            demoCustomer.Id,
            admin.Id,
            DocumentTypes.CitizenIdBack,
            "001204000001",
            DateTime.Today.AddYears(10));

        await EnsureDemoDocumentAsync(
            dbContext,
            demoCustomer.Id,
            admin.Id,
            DocumentTypes.DrivingLicenseBack,
            "790000000001",
            DateTime.Today.AddYears(5));

        // Đồng bộ dữ liệu KYC chi tiết cho cả database demo cũ và database mới.
        var demoDocuments = await dbContext.CustomerDocuments
            .Where(item => item.CustomerId == demoCustomer.Id)
            .ToListAsync();
        var citizenFront = demoDocuments.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenId);
        var citizenBack = demoDocuments.FirstOrDefault(item => item.DocumentType == DocumentTypes.CitizenIdBack);
        var drivingLicense = demoDocuments.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicense);
        var drivingLicenseBack = demoDocuments.FirstOrDefault(item => item.DocumentType == DocumentTypes.DrivingLicenseBack);

        if (citizenFront is not null && citizenBack is not null)
        {
            citizenFront.ExpiryDate ??= DateTime.Today.AddYears(10);
            citizenBack.ExpiryDate ??= citizenFront.ExpiryDate;
            await dbContext.SaveChangesAsync();

            var demoName = "Khách hàng Demo";
            var demoBirthDate = new DateTime(1995, 1, 15);
            var demoGender = "Nam";
            var demoIssuedDate = DateTime.Today.AddYears(-2);
            var demoAddress = "Hà Nội";

            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE [CustomerDocuments]
                SET [FullNameOnDocument] = {demoName},
                    [DateOfBirth] = {demoBirthDate},
                    [Gender] = {demoGender},
                    [IssuedDate] = {demoIssuedDate},
                    [PermanentAddress] = {demoAddress}
                WHERE [CustomerDocumentId] IN ({citizenFront.CustomerDocumentId}, {citizenBack.CustomerDocumentId})");
        }

        if (drivingLicense is not null && drivingLicenseBack is not null)
        {
            drivingLicense.ExpiryDate ??= DateTime.Today.AddYears(5);
            drivingLicenseBack.ExpiryDate ??= drivingLicense.ExpiryDate;
            await dbContext.SaveChangesAsync();

            var demoName = "Khách hàng Demo";
            var demoIssuedDate = DateTime.Today.AddYears(-3);
            var demoLicenseClass = "B";

            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE [CustomerDocuments]
                SET [FullNameOnDocument] = {demoName},
                    [IssuedDate] = {demoIssuedDate},
                    [LicenseClass] = {demoLicenseClass}
                WHERE [CustomerDocumentId] IN ({drivingLicense.CustomerDocumentId}, {drivingLicenseBack.CustomerDocumentId})");
        }

        if (!await dbContext.Promotions.AnyAsync())
        {
            dbContext.Promotions.Add(new Promotion
            {
                Code = "WELCOME10",
                Name = "Chào mừng khách hàng mới",
                PromotionType = PromotionType.Percentage,
                Value = 10,
                MaximumDiscount = 300_000m,
                MinimumRentalAmount = 500_000m,
                StartAt = DateTime.Today.AddMonths(-1),
                EndAt = DateTime.Today.AddYears(2),
                UsageLimit = 500,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            await dbContext.SaveChangesAsync();
        }
    }

    private static async Task EnsureDemoDocumentAsync(
        ApplicationDbContext dbContext,
        string customerId,
        string adminId,
        string documentType,
        string documentNumber,
        DateTime expiryDate)
    {
        if (await dbContext.CustomerDocuments.AnyAsync(item =>
                item.CustomerId == customerId && item.DocumentType == documentType))
        {
            return;
        }

        var now = DateTime.UtcNow;
        dbContext.CustomerDocuments.Add(new CustomerDocument
        {
            CustomerId = customerId,
            DocumentType = documentType,
            DocumentNumber = documentNumber,
            ExpiryDate = expiryDate,
            ImagePath = "/images/demo/document-placeholder.svg",
            Status = DocumentStatus.Verified,
            VerifiedBy = adminId,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<ApplicationUser> EnsureUserAsync(
        UserManager<ApplicationUser> userManager,
        string email,
        string fullName,
        string phoneNumber,
        string roleName)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                FullName = fullName,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                PhoneNumber = phoneNumber,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var createResult = await userManager.CreateAsync(user, DemoPassword);
            EnsureSucceeded(createResult, $"Không thể tạo tài khoản {email}");
        }

        if (!await userManager.IsInRoleAsync(user, roleName))
        {
            var addRoleResult = await userManager.AddToRoleAsync(user, roleName);
            EnsureSucceeded(addRoleResult, $"Không thể gán vai trò {roleName} cho {email}");
        }

        return user;
    }

    private static Vehicle CreateVehicle(
        int brandId,
        string name,
        string model,
        string licensePlate,
        int year,
        int seats,
        string transmission,
        string fuelType,
        string color,
        decimal dailyPrice,
        int mileage,
        string imagePath)
    {
        var today = DateTime.Today;
        var vehicle = new Vehicle
        {
            BrandId = brandId,
            VehicleName = name,
            Model = model,
            LicensePlate = licensePlate,
            ManufactureYear = year,
            Seats = seats,
            Transmission = transmission,
            FuelType = fuelType,
            Color = color,
            DailyPrice = dailyPrice,
            CurrentMileage = mileage,
            Status = VehicleStatus.Available,
            Description = "Xe thuộc đội xe SmartCar, được kiểm tra trước mỗi lượt thuê.",
            CreatedAt = DateTime.UtcNow
        };

        vehicle.Images.Add(new VehicleImage
        {
            ImagePath = imagePath,
            IsPrimary = true,
            SortOrder = 0
        });

        vehicle.Documents.Add(new VehicleDocument
        {
            DocumentType = VehicleDocumentType.Registration,
            DocumentNumber = $"DK-{licensePlate}",
            IssuedDate = today.AddYears(-1),
            ExpiryDate = null,
            Notes = "Dữ liệu demo"
        });
        vehicle.Documents.Add(new VehicleDocument
        {
            DocumentType = VehicleDocumentType.Inspection,
            DocumentNumber = $"DKIEM-{licensePlate}",
            IssuedDate = today.AddMonths(-3),
            ExpiryDate = today.AddYears(2),
            Notes = "Dữ liệu demo"
        });
        vehicle.Documents.Add(new VehicleDocument
        {
            DocumentType = VehicleDocumentType.Insurance,
            DocumentNumber = $"BH-{licensePlate}",
            IssuedDate = today.AddMonths(-1),
            ExpiryDate = today.AddYears(1),
            Notes = "Dữ liệu demo"
        });
        vehicle.Documents.Add(new VehicleDocument
        {
            DocumentType = VehicleDocumentType.RoadFee,
            DocumentNumber = $"PDB-{licensePlate}",
            IssuedDate = today.AddMonths(-1),
            ExpiryDate = today.AddYears(1),
            Notes = "Dữ liệu demo"
        });

        return vehicle;
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                message + ": " + string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }
}
