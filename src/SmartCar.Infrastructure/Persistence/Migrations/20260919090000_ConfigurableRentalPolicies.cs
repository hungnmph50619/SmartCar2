using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260919090000_ConfigurableRentalPolicies")]
public sealed class ConfigurableRentalPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "PolicyJson", table: "BusinessSettings", type: "nvarchar(max)", nullable: true);
        // NULL on legacy bookings deliberately retains the historical constant policy.
        migrationBuilder.AddColumn<string>(name: "PolicyJson", table: "Bookings", type: "nvarchar(max)", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PolicyJson", table: "Bookings");
        migrationBuilder.DropColumn(name: "PolicyJson", table: "BusinessSettings");
    }
}
