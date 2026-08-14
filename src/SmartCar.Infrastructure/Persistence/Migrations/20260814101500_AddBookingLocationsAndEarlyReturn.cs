using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260814101500_AddBookingLocationsAndEarlyReturn")]
public partial class AddBookingLocationsAndEarlyReturn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PickupLocation",
            table: "Bookings",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReturnLocation",
            table: "Bookings",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReturnLocation",
            table: "VehicleReturns",
            type: "nvarchar(max)",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PickupLocation",
            table: "Bookings");

        migrationBuilder.DropColumn(
            name: "ReturnLocation",
            table: "Bookings");

        migrationBuilder.DropColumn(
            name: "ReturnLocation",
            table: "VehicleReturns");
    }
}
