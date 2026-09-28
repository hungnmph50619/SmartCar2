using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260927120000_StaffBookingClaim")]
public sealed class StaffBookingClaim : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "HandlingStaffId",
            table: "Bookings",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "HandlingLeaseExpiresAt",
            table: "Bookings",
            type: "datetime2",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "HandlingLeaseExpiresAt", table: "Bookings");
        migrationBuilder.DropColumn(name: "HandlingStaffId", table: "Bookings");
    }
}
