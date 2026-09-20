using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.Controllers;
using SmartCar.Web.Services;
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

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"return-edit-damage-{Guid.NewGuid():N}")
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

        public Task<IReadOnlyList<SmartCar.Application.Features.Audits.AuditLogDto>> GetRecentAsync(
            int take,
            CancellationToken cancellationToken,
            bool unused = false) =>
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
