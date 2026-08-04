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
    private static readonly string[] RequiredBrandNames =
    {
        "Toyota",
        "Honda",
        "Hyundai",
        "Kia",
        "Ford",
        "Mazda"
    };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var dbContext = services.GetRequiredService<ApplicationDbContext>();

        foreach (var roleName in new[] { RoleNames.Customer, RoleNames.Manager })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(roleName));
                if (!roleResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Không thể tạo vai trò {roleName}: " +
                        string.Join("; ", roleResult.Errors.Select(error => error.Description)));
                }
            }
        }

        const string managerEmail = "manager@smartcar.vn";
        const string managerPassword = "SmartCar@123";

        var manager = await userManager.FindByEmailAsync(managerEmail);
        if (manager is null)
        {
            manager = new ApplicationUser
            {
                FullName = "Quản lý SmartCar",
                UserName = managerEmail,
                Email = managerEmail,
                EmailConfirmed = true,
                PhoneNumber = "0900000000",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var createResult = await userManager.CreateAsync(manager, managerPassword);
            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Không thể tạo tài khoản quản lý: " +
                    string.Join("; ", createResult.Errors.Select(error => error.Description)));
            }
        }

        if (!await userManager.IsInRoleAsync(manager, RoleNames.Manager))
        {
            var addRoleResult = await userManager.AddToRoleAsync(manager, RoleNames.Manager);
            if (!addRoleResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Không thể gán vai trò Manager: " +
                    string.Join("; ", addRoleResult.Errors.Select(error => error.Description)));
            }
        }

        var existingBrands = await dbContext.Brands.ToListAsync();
        var existingBrandNames = existingBrands
            .Select(brand => brand.BrandName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var brandName in RequiredBrandNames)
        {
            if (!existingBrandNames.Contains(brandName))
            {
                dbContext.Brands.Add(new Brand { BrandName = brandName });
            }
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync();
        }

        if (!await dbContext.Vehicles.AnyAsync())
        {
            var brandList = await dbContext.Brands
                .AsNoTracking()
                .ToListAsync();
            var brands = brandList.ToDictionary(
                brand => brand.BrandName,
                StringComparer.OrdinalIgnoreCase);

            dbContext.Vehicles.AddRange(
                CreateVehicle(brands["Toyota"].BrandId, "Toyota Vios 2024", "Vios", "30A-123.45", 2024, 5, "Tự động", "Xăng", "Trắng", 700_000m, 18_250,
                    "Sedan 5 chỗ dễ điều khiển, tiết kiệm nhiên liệu, phù hợp đi nội thành và các chuyến công tác ngắn ngày."),
                CreateVehicle(brands["Honda"].BrandId, "Honda City 2024", "City", "30A-234.56", 2024, 5, "Tự động", "Xăng", "Đen", 750_000m, 15_800,
                    "Không gian rộng, vận hành ổn định và phù hợp cho gia đình nhỏ hoặc nhu cầu đi công việc."),
                CreateVehicle(brands["Mazda"].BrandId, "Mazda CX-5 2024", "CX-5", "30A-345.67", 2024, 5, "Tự động", "Xăng", "Trắng", 900_000m, 12_600,
                    "SUV 5 chỗ có khoang hành lý rộng, nội thất tiện nghi và phù hợp cho chuyến đi gia đình."),
                CreateVehicle(brands["Toyota"].BrandId, "Toyota Innova 2023", "Innova", "30A-456.78", 2023, 7, "Tự động", "Xăng", "Bạc", 900_000m, 32_100,
                    "Xe 7 chỗ rộng rãi, phù hợp nhóm đông người, chuyến đi đường dài và nhiều hành lý."),
                CreateVehicle(brands["Kia"].BrandId, "Kia Carens 2025", "Carens", "30A-567.89", 2025, 7, "Tự động", "Xăng", "Đỏ", 980_000m, 6_500,
                    "MPV 7 chỗ đời mới, thiết kế hiện đại, phù hợp gia đình và chuyến du lịch dài ngày."),
                CreateVehicle(brands["Hyundai"].BrandId, "Hyundai Accent 2024", "Accent", "30A-678.90", 2024, 5, "Tự động", "Xăng", "Trắng", 720_000m, 14_300,
                    "Sedan nhỏ gọn, tiết kiệm chi phí và thuận tiện khi di chuyển trong thành phố."),
                CreateVehicle(brands["Ford"].BrandId, "Ford Everest 2023", "Everest", "30A-789.01", 2023, 7, "Tự động", "Dầu", "Xanh đậm", 1_350_000m, 28_400,
                    "SUV 7 chỗ mạnh mẽ, khoang hành lý lớn và phù hợp các chuyến đi dài hoặc địa hình đa dạng."),
                CreateVehicle(brands["Toyota"].BrandId, "Toyota Corolla Cross 2024", "Corolla Cross", "30A-890.12", 2024, 5, "Tự động", "Hybrid", "Xanh", 1_050_000m, 9_700,
                    "Crossover 5 chỗ tiết kiệm nhiên liệu, vận hành êm và có nhiều trang bị hỗ trợ an toàn."));

            await dbContext.SaveChangesAsync();
        }
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
        string description) => new()
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
            Description = description,
            CreatedAt = DateTime.UtcNow
        };
}
