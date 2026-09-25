using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260925120000_BookingSource")]
public sealed class BookingSourceMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Source", table: "Bookings", type: "nvarchar(30)",
            maxLength: 30, nullable: false, defaultValue: "CustomerWeb");
        migrationBuilder.Sql("""
            UPDATE b SET [Source] = N'StaffCounter'
            FROM [Bookings] b
            WHERE EXISTS (
                SELECT 1 FROM [AuditLogs] a
                WHERE a.[Action] = N'StaffCreateCounterRental'
                  AND a.[EntityName] = N'Booking'
                  AND a.[EntityId] = CONVERT(nvarchar(20), b.[BookingId]));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "Source", table: "Bookings");
}
