using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCar.Application.Features.Audits;
using SmartCar.Application.Features.Incidents;
using SmartCar.Domain.Entities;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using Xunit;

namespace SmartCar.Tests;

public sealed class IncidentVehicleStateTests
{
    [Theory]
    [InlineData(VehicleStatus.Rented, VehicleStatus.Rented)]
    [InlineData(VehicleStatus.Inspection, VehicleStatus.Inspection)]
    [InlineData(VehicleStatus.Inactive, VehicleStatus.Inactive)]
    [InlineData(VehicleStatus.Available, VehicleStatus.Maintenance)]
    public async Task CreateIncident_PreservesRentalInspectionAndInactiveState(
        VehicleStatus initialStatus, VehicleStatus expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection, initialStatus);
        var service = CreateIncidentService(db);

        var result = await service.CreateAsync(new CreateIncidentRequest(
            1, null, IncidentType.Damage, DateTime.Now.AddMinutes(-1), null,
            "Vết xước cần kiểm tra", 100_000m, 0m, 0m, null, null), string.Empty);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(expectedStatus, (await db.Vehicles.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(IncidentStatus.Open, (await db.VehicleIncidents.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(VehicleStatus.Rented, VehicleStatus.Rented)]
    [InlineData(VehicleStatus.Inspection, VehicleStatus.Inspection)]
    [InlineData(VehicleStatus.Inactive, VehicleStatus.Inactive)]
    [InlineData(VehicleStatus.Maintenance, VehicleStatus.Available)]
    public async Task ResolveIncident_RestoresSafeVehicleStateWithoutCreatingMaintenance(
        VehicleStatus initialStatus, VehicleStatus expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateDbAsync(connection, initialStatus);
        db.VehicleIncidents.Add(new VehicleIncident
        {
            VehicleId = 1, IncidentType = IncidentType.Damage,
            Description = "Vết xước cần sửa", OccurredAt = DateTime.Now.AddHours(-1)
        });
        await db.SaveChangesAsync();
        var incident = await db.VehicleIncidents.SingleAsync();

        var result = await CreateIncidentService(db).ResolveAsync(
            new ResolveIncidentRequest(incident.VehicleIncidentId, 100_000m, 0m, 0m, null),
            string.Empty);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(expectedStatus, (await db.Vehicles.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.MaintenanceRecords.ToListAsync());
        Assert.Equal(100_000m, (await db.VehicleIncidents.SingleAsync()).ActualCost);
    }

    private static async Task<TestDbContext> CreateDbAsync(SqliteConnection connection, VehicleStatus status)
    {
        var db = new TestDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Brands.Add(new Brand { BrandId = 1, BrandName = "Test" });
        db.Vehicles.Add(new Vehicle
        {
            VehicleId = 1, BrandId = 1, VehicleName = "Test car", LicensePlate = "30A-12345",
            Status = status, RowVersion = new byte[] { 1 }
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static IIncidentService CreateIncidentService(ApplicationDbContext db) =>
        CreateService<IIncidentService>("IncidentService", db,
            CreateService<IAuditService>("AuditService", db));

    private static T CreateService<T>(string name, params object[] dependencies)
    {
        var type = typeof(ApplicationDbContext).Assembly.GetType(
            $"SmartCar.Infrastructure.Services.{name}", throwOnError: true)!;
        return (T)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: dependencies, culture: null)!;
    }

    private sealed class TestDbContext : ApplicationDbContext
    {
        public TestDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Vehicle>().Property(vehicle => vehicle.RowVersion).ValueGeneratedNever();
        }
    }
}
