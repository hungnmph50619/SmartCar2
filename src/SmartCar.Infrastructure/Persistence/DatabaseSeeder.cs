using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Entities;
using SmartCar.Infrastructure.Identity;

namespace SmartCar.Infrastructure.Persistence;

public static class DatabaseSeeder
{
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
                if (!roleResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Không thể tạo vai trò {roleName}: " +
                        string.Join("; ", roleResult.Errors.Select(error => error.Description)));
                }
            }
        }

        const string adminEmail = "admin@smartcar.vn";
        const string adminPassword = "SmartCar@123";

        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                FullName = "Quản trị SmartCar",
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true,
                PhoneNumber = "0900000000",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var createResult = await userManager.CreateAsync(admin, adminPassword);
            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Không thể tạo tài khoản Admin: " +
                    string.Join("; ", createResult.Errors.Select(error => error.Description)));
            }
        }

        if (!await userManager.IsInRoleAsync(admin, RoleNames.Admin))
        {
            var addRoleResult = await userManager.AddToRoleAsync(admin, RoleNames.Admin);
            if (!addRoleResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Không thể gán vai trò Admin: " +
                    string.Join("; ", addRoleResult.Errors.Select(error => error.Description)));
            }
        }

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
    }
}
