using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260812154500_RemovePromotions")]
public sealed class RemovePromotions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[Promotions]', N'U') IS NOT NULL
    DROP TABLE [dbo].[Promotions];

IF COL_LENGTH('dbo.Bookings', 'PromotionCode') IS NOT NULL
    ALTER TABLE [dbo].[Bookings] DROP COLUMN [PromotionCode];

IF COL_LENGTH('dbo.Bookings', 'DiscountAmount') IS NOT NULL
    ALTER TABLE [dbo].[Bookings] DROP COLUMN [DiscountAmount];
");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
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

        migrationBuilder.CreateIndex(
            name: "IX_Promotions_Code",
            table: "Promotions",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Promotions_IsActive_StartAt_EndAt",
            table: "Promotions",
            columns: new[] { "IsActive", "StartAt", "EndAt" });
    }
}
