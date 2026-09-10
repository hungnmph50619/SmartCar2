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
        // Chịu được database đã được cập nhật/sửa dở trước đó.
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.Bookings', N'ReservationExpiresAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Bookings] ADD [ReservationExpiresAt] datetime2 NULL;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.Bookings', N'ReservationExpiresAt') IS NOT NULL
            BEGIN
                ALTER TABLE [dbo].[Bookings] DROP COLUMN [ReservationExpiresAt];
            END;
            """);
    }
}
