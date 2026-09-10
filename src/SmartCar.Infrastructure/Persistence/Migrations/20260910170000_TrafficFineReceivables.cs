using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260910170000_TrafficFineReceivables")]
public sealed class TrafficFineReceivables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "VehicleIncidentId",
            table: "Payments",
            type: "int",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Payments_VehicleIncidentId",
            table: "Payments",
            column: "VehicleIncidentId");

        migrationBuilder.AddForeignKey(
            name: "FK_Payments_VehicleIncidents_VehicleIncidentId",
            table: "Payments",
            column: "VehicleIncidentId",
            principalTable: "VehicleIncidents",
            principalColumn: "VehicleIncidentId",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Payments_VehicleIncidents_VehicleIncidentId",
            table: "Payments");

        migrationBuilder.DropIndex(
            name: "IX_Payments_VehicleIncidentId",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "VehicleIncidentId",
            table: "Payments");
    }
}
