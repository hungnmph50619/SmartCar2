using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260814114000_AddDeliveryPricing")]
public partial class AddDeliveryPricing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PickupMethod",
            table: "Bookings",
            type: "nvarchar(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "SelfPickup");

        migrationBuilder.AddColumn<decimal>(
            name: "PickupDeliveryDistanceKm",
            table: "Bookings",
            type: "decimal(8,1)",
            precision: 8,
            scale: 1,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<decimal>(
            name: "ReturnCollectionDistanceKm",
            table: "Bookings",
            type: "decimal(8,1)",
            precision: 8,
            scale: 1,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<decimal>(
            name: "DeliveryRatePerKm",
            table: "Bookings",
            type: "decimal(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<decimal>(
            name: "DeliveryFee",
            table: "Bookings",
            type: "decimal(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            defaultValue: 0m);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PickupMethod", table: "Bookings");
        migrationBuilder.DropColumn(name: "PickupDeliveryDistanceKm", table: "Bookings");
        migrationBuilder.DropColumn(name: "ReturnCollectionDistanceKm", table: "Bookings");
        migrationBuilder.DropColumn(name: "DeliveryRatePerKm", table: "Bookings");
        migrationBuilder.DropColumn(name: "DeliveryFee", table: "Bookings");
    }
}
