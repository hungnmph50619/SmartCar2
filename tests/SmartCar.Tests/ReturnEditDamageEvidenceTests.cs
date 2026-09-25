using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using SmartCar.Application.Features.Audits;
using SmartCar.Web.ViewModels;
using Xunit;

namespace SmartCar.Tests;

public sealed class ReturnEditDamageEvidenceTests
{
    [Fact]
    public async Task Edit_RejectsDamageFlagWhenFinalEvidenceHasNoDamagePhoto()
    {
        await using var db = CreateDbContext();
        var now = DateTime.Now;

        db.Brands.Add(new Brand
        {
            BrandId = 1,
            BrandName = "Test Brand"
        });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 10,
            BrandId = 1,
            VehicleName = "Test Car",
            LicensePlate = "30A-12345",
            ManufactureYear = 2025,
            Seats = 5,
            Transmission = "AT",
            FuelType = "Gasoline",
            DailyPrice = 700_000m,
            CurrentMileage = 1_000,
            Status = VehicleStatus.Inspection,
            RowVersion = new byte[] { 1 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 701,
            CustomerId = "customer-1",
            VehicleId = 10,
            PickupDate = now.AddDays(-2),
            ReturnDate = now.AddHours(-2),
            DailyPrice = 700_000m,
            NumberOfDays = 1,
            RentalAmount = 700_000m,
            DepositAmount = 0m,
            AdditionalAmount = 0m,
            TotalAmount = 700_000m,
            Status = BookingStatus.PendingInspection,
            RowVersion = new byte[] { 1 }
        });
        db.VehicleHandovers.Add(new VehicleHandover
        {
            BookingId = 701,
            HandoverAt = now.AddDays(-2),
            Mileage = 1_000,
            FuelLevel = "100%",
            IncludedKilometers = 300,
            ExcessKmFeePerKm = 5_000m,
            LateReturnFeeMultiplier = 1.5m,
            TrafficFineTerms = "Test",
            DamageCompensationTerms = "Test",
            PenaltyPolicyAccepted = true,
            ImagePaths = "/uploads/handovers/701/front.png"
        });
        db.VehicleReturns.Add(new VehicleReturn
        {
            BookingId = 701,
            ReturnedAt = now.AddHours(-1),
            Mileage = 1_050,
            FuelLevel = "80%",
            AccessoryStatus = "Đủ",
            ImagePaths = string.Join(
                ';',
                Enumerable.Range(1, 7)
                    .Select(index => $"/uploads/returns/701/other-{index}.png"))
        });
        await db.SaveChangesAsync();

        var controller = new ReturnEditsController(
            db,
            new AuditServiceStub(),
            new TestWebHostEnvironment());

        var result = await controller.Edit(
            new ReturnEditViewModel
            {
                BookingId = 701,
                ReturnedAt = now.AddHours(-1),
                Mileage = 1_050,
                FuelLevel = "80",
                AccessoryStatus = "Đủ",
                HasDamage = true,
                NewImages = new List<Microsoft.AspNetCore.Http.IFormFile>()
            },
            default);

        Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result);
        Assert.Contains(
            controller.ModelState.Values.SelectMany(value => value.Errors),
            error => error.ErrorMessage != null &&
                     error.ErrorMessage.Contains(
                         "ảnh hư hỏng",
                         StringComparison.OrdinalIgnoreCase));

        db.ChangeTracker.Clear();
        var saved = await db.VehicleReturns.SingleAsync(item => item.BookingId == 701);
        Assert.False(saved.HasDamage);
    }

    [Fact]
    public async Task Edit_RemovesDamageChargeWhenDamageBasisIsCleared()
    {
        await using var db = CreateDbContext();
        var now = DateTime.Now;

        db.Brands.Add(new Brand
        {
            BrandId = 2,
            BrandName = "Test Brand 2"
        });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 20,
            BrandId = 2,
            VehicleName = "Test Car 2",
            LicensePlate = "30A-54321",
            ManufactureYear = 2025,
            Seats = 5,
            Transmission = "AT",
            FuelType = "Gasoline",
            DailyPrice = 700_000m,
            CurrentMileage = 2_050,
            Status = VehicleStatus.Inspection,
            RowVersion = new byte[] { 2 }
        });
        db.Bookings.Add(new Booking
        {
            BookingId = 702,
            CustomerId = "customer-2",
            VehicleId = 20,
            PickupDate = now.AddDays(-1),
            ReturnDate = now.AddHours(1),
            DailyPrice = 700_000m,
            NumberOfDays = 1,
            RentalAmount = 700_000m,
            DepositAmount = 0m,
            AdditionalAmount = 250_000m,
            TotalAmount = 950_000m,
            Status = BookingStatus.PendingInspection,
            RowVersion = new byte[] { 2 }
        });
        db.VehicleHandovers.Add(new VehicleHandover
        {
            BookingId = 702,
            HandoverAt = now.AddDays(-1),
            Mileage = 2_000,
            FuelLevel = "80%",
            IncludedKilometers = 300,
            ExcessKmFeePerKm = 5_000m,
            LateReturnFeeMultiplier = 1.5m,
            TrafficFineTerms = "Test",
            DamageCompensationTerms = "Test",
            PenaltyPolicyAccepted = true,
            ImagePaths = "/uploads/handovers/702/front-test.png"
        });
        db.VehicleReturns.Add(new VehicleReturn
        {
            BookingId = 702,
            ReturnedAt = now,
            Mileage = 2_050,
            FuelLevel = "80%",
            AccessoryStatus = "Đủ",
            HasDamage = true,
            ImagePaths = string.Join(';', new[]
            {
                "/uploads/returns/702/front-test.png",
                "/uploads/returns/702/rear-test.png",
                "/uploads/returns/702/left-test.png",
                "/uploads/returns/702/right-test.png",
                "/uploads/returns/702/interior-test.png",
                "/uploads/returns/702/odometer-test.png",
                "/uploads/returns/702/fuel-test.png",
                "/uploads/returns/702/damage-test.png"
            }),
            AdditionalCharges = new List<AdditionalCharge>
            {
                new()
                {
                    ChargeType = AdditionalChargeType.Damage,
                    Description = "Trầy xước",
                    Amount = 250_000m
                }
            }
        });
        await db.SaveChangesAsync();

        var controller = new ReturnEditsController(
            db,
            new AuditServiceStub(),
            new TestWebHostEnvironment());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "staff-1") },
                    "test"))
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(
            httpContext,
            new TempDataProviderStub());

        var result = await controller.Edit(
            new ReturnEditViewModel
            {
                BookingId = 702,
                Mileage = 2_050,
                FuelLevel = "80",
                AccessoryStatus = "Đủ",
                HasDamage = false,
                ImagesToDelete = new List<string>
                {
                    "/uploads/returns/702/damage-test.png"
                }
            },
            default);

        Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(result);

        db.ChangeTracker.Clear();
        var saved = await db.VehicleReturns
            .Include(item => item.AdditionalCharges)
            .SingleAsync(item => item.BookingId == 702);

        Assert.False(saved.HasDamage);
        Assert.DoesNotContain(
            saved.AdditionalCharges,
            charge => charge.ChargeType == AdditionalChargeType.Damage);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"return-edit-damage-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(
                InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SmartCar.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TempDataProviderStub : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
        }
    }

    private sealed class AuditServiceStub : IAuditService
    {
        public Task WriteAsync(
            string? userId,
            string action,
            string entityName,
            string entityId,
            string description,
            string? oldValues = null,
            string? newValues = null,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SmartCar.Application.Features.Audits.AuditLogDto>> GetRecentAsync(
            int take = 200,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SmartCar.Application.Features.Audits.AuditLogSearchResult> SearchAsync(
            SmartCar.Application.Features.Audits.AuditLogQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SmartCar.Application.Features.Audits.AuditLogDto?> GetByIdAsync(
            long auditLogId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

// CI trigger for the red regression test.
 
