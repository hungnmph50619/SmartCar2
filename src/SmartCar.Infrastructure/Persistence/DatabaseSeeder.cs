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
