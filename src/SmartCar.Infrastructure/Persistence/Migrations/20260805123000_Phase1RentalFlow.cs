using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260805123000_Phase1RentalFlow")]
public sealed class Phase1RentalFlow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Payments_BookingId",
            table: "Payments");

        migrationBuilder.AddColumn<string>(
            name: "Type",
            table: "Payments",
            type: "nvarchar(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "Rental");

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "Vehicles",
            type: "rowversion",
            rowVersion: true,
            nullable: false,
            defaultValue: Array.Empty<byte>());

        migrationBuilder.AddColumn<byte[]>(
            name: "RowVersion",
            table: "Bookings",
            type: "rowversion",
            rowVersion: true,
            nullable: false,
            defaultValue: Array.Empty<byte>());

        migrationBuilder.AddColumn<string>(
            name: "ChargeType",
            table: "AdditionalCharges",
            type: "nvarchar(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "Other");

        migrationBuilder.CreateIndex(
            name: "IX_Payments_BookingId_Type_Status",
            table: "Payments",
            columns: new[] { "BookingId", "Type", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Payments_BookingId_Type_Status",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "Type",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "Vehicles");

        migrationBuilder.DropColumn(
            name: "RowVersion",
            table: "Bookings");

        migrationBuilder.DropColumn(
            name: "ChargeType",
            table: "AdditionalCharges");

        migrationBuilder.CreateIndex(
            name: "IX_Payments_BookingId",
            table: "Payments",
            column: "BookingId",
            unique: true);
    }
}
