using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260805133000_Phase2Operations")]
public sealed class Phase2Operations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CustomerDocuments_CustomerId_DocumentType",
            table: "CustomerDocuments");

        migrationBuilder.AddColumn<string>(
            name: "CancelledBy",
            table: "Bookings",
            type: "nvarchar(30)",
            maxLength: 30,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CancelledAt",
            table: "Bookings",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "NoShowMarkedAt",
            table: "Bookings",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "RefundAmount",
            table: "Bookings",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<string>(
            name: "RefundReason",
            table: "Bookings",
            type: "nvarchar(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "VerifiedBy",
            table: "CustomerDocuments",
            type: "nvarchar(450)",
            maxLength: 450,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "VerifiedAt",
            table: "CustomerDocuments",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "UpdatedAt",
            table: "CustomerDocuments",
            type: "datetime2",
            nullable: false,
            defaultValueSql: "GETUTCDATE()");

        migrationBuilder.AddColumn<bool>(
            name: "IsLateReturn",
            table: "VehicleReturns",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "LateMinutes",
            table: "VehicleReturns",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<decimal>(
            name: "LateFee",
            table: "VehicleReturns",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.CreateTable(
            name: "BookingExtensions",
            columns: table => new
            {
                BookingExtensionId = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BookingId = table.Column<int>(type: "int", nullable: false),
                OriginalReturnDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                RequestedReturnDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                AdditionalDays = table.Column<int>(type: "int", nullable: false),
                AdditionalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                CustomerNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                AdminNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                PaidAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BookingExtensions", x => x.BookingExtensionId);
                table.ForeignKey(
                    name: "FK_BookingExtensions_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "BookingId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CustomerDocuments_CustomerId_DocumentType",
            table: "CustomerDocuments",
            columns: new[] { "CustomerId", "DocumentType" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BookingExtensions_BookingId_Status",
            table: "BookingExtensions",
            columns: new[] { "BookingId", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BookingExtensions");

        migrationBuilder.DropIndex(
            name: "IX_CustomerDocuments_CustomerId_DocumentType",
            table: "CustomerDocuments");

        migrationBuilder.DropColumn(name: "CancelledBy", table: "Bookings");
        migrationBuilder.DropColumn(name: "CancelledAt", table: "Bookings");
        migrationBuilder.DropColumn(name: "NoShowMarkedAt", table: "Bookings");
        migrationBuilder.DropColumn(name: "RefundAmount", table: "Bookings");
        migrationBuilder.DropColumn(name: "RefundReason", table: "Bookings");
        migrationBuilder.DropColumn(name: "VerifiedBy", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "VerifiedAt", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "UpdatedAt", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "IsLateReturn", table: "VehicleReturns");
        migrationBuilder.DropColumn(name: "LateMinutes", table: "VehicleReturns");
        migrationBuilder.DropColumn(name: "LateFee", table: "VehicleReturns");

        migrationBuilder.CreateIndex(
            name: "IX_CustomerDocuments_CustomerId_DocumentType",
            table: "CustomerDocuments",
            columns: new[] { "CustomerId", "DocumentType" });
    }
}
