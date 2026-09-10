using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260910163000_BookingReservationExpiry")]
public sealed class BookingReservationExpiry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "ReservationExpiresAt",
            table: "Bookings",
            type: "datetime2",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ReservationExpiresAt",
            table: "Bookings");
    }
}
