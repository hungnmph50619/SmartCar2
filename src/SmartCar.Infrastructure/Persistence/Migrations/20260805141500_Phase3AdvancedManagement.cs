using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260805141500_Phase3AdvancedManagement")]
public sealed class Phase3AdvancedManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "DiscountAmount",
            table: "Bookings",
            type: "decimal(18,2)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<string>(
            name: "PromotionCode",
            table: "Bookings",
            type: "nvarchar(50)",
            maxLength: 50,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AuditLogs",
            columns: table => new
            {
                AuditLogId = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                EntityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                OldValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                NewValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                IpAddress = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditLogs", x => x.AuditLogId);
            });

        migrationBuilder.CreateTable(
            name: "Promotions",
            columns: table => new
            {
                PromotionId = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                PromotionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                Value = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                MaximumDiscount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                MinimumRentalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                StartAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                EndAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UsageLimit = table.Column<int>(type: "int", nullable: true),
                UsedCount = table.Column<int>(type: "int", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Promotions", x => x.PromotionId);
            });

        migrationBuilder.CreateTable(
            name: "VehicleDocuments",
            columns: table => new
            {
                VehicleDocumentId = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                VehicleId = table.Column<int>(type: "int", nullable: false),
                DocumentType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                DocumentNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                IssuedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                ImagePath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VehicleDocuments", x => x.VehicleDocumentId);
                table.ForeignKey(
                    name: "FK_VehicleDocuments_Vehicles_VehicleId",
                    column: x => x.VehicleId,
                    principalTable: "Vehicles",
                    principalColumn: "VehicleId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "VehicleIncidents",
            columns: table => new
            {
                VehicleIncidentId = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                VehicleId = table.Column<int>(type: "int", nullable: false),
                BookingId = table.Column<int>(type: "int", nullable: true),
                IncidentType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Location = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                Description = table.Column<string>(type: "nvarchar(1500)", maxLength: 1500, nullable: false),
                EstimatedCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                ActualCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                FineAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                CustomerLiabilityAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                EvidencePaths = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VehicleIncidents", x => x.VehicleIncidentId);
                table.ForeignKey(
                    name: "FK_VehicleIncidents_Bookings_BookingId",
                    column: x => x.BookingId,
                    principalTable: "Bookings",
                    principalColumn: "BookingId",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VehicleIncidents_Vehicles_VehicleId",
                    column: x => x.VehicleId,
                    principalTable: "Vehicles",
                    principalColumn: "VehicleId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_EntityName_EntityId_CreatedAt",
            table: "AuditLogs",
            columns: new[] { "EntityName", "EntityId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_UserId_CreatedAt",
            table: "AuditLogs",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Promotions_Code",
            table: "Promotions",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Promotions_IsActive_StartAt_EndAt",
            table: "Promotions",
            columns: new[] { "IsActive", "StartAt", "EndAt" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleDocuments_VehicleId_DocumentType_ExpiryDate",
            table: "VehicleDocuments",
            columns: new[] { "VehicleId", "DocumentType", "ExpiryDate" });

        migrationBuilder.CreateIndex(
            name: "IX_VehicleIncidents_BookingId",
            table: "VehicleIncidents",
            column: "BookingId");

        migrationBuilder.CreateIndex(
            name: "IX_VehicleIncidents_VehicleId_Status_OccurredAt",
            table: "VehicleIncidents",
            columns: new[] { "VehicleId", "Status", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AuditLogs");
        migrationBuilder.DropTable(name: "Promotions");
        migrationBuilder.DropTable(name: "VehicleDocuments");
        migrationBuilder.DropTable(name: "VehicleIncidents");

        migrationBuilder.DropColumn(name: "DiscountAmount", table: "Bookings");
        migrationBuilder.DropColumn(name: "PromotionCode", table: "Bookings");
    }
}
