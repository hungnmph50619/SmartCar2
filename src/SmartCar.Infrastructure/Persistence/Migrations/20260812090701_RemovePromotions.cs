using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemovePromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hỗ trợ cả database cũ đã từng có module khuyến mãi và database mới.
            // SQL Server không cho DROP COLUMN nếu cột còn default constraint,
            // vì vậy cần gỡ constraint động trước khi xóa cột.
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[Promotions]', N'U') IS NOT NULL
    DROP TABLE [dbo].[Promotions];

DECLARE @ConstraintName sysname;

IF COL_LENGTH('dbo.Bookings', 'DiscountAmount') IS NOT NULL
BEGIN
    SET @ConstraintName = NULL;

    SELECT @ConstraintName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.object_id = dc.parent_object_id
       AND c.column_id = dc.parent_column_id
    INNER JOIN sys.tables t
        ON t.object_id = c.object_id
    INNER JOIN sys.schemas s
        ON s.schema_id = t.schema_id
    WHERE s.name = N'dbo'
      AND t.name = N'Bookings'
      AND c.name = N'DiscountAmount';

    IF @ConstraintName IS NOT NULL
        EXEC(N'ALTER TABLE [dbo].[Bookings] DROP CONSTRAINT ' + QUOTENAME(@ConstraintName));

    ALTER TABLE [dbo].[Bookings] DROP COLUMN [DiscountAmount];
END;

IF COL_LENGTH('dbo.Bookings', 'PromotionCode') IS NOT NULL
BEGIN
    SET @ConstraintName = NULL;

    SELECT @ConstraintName = dc.name
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.object_id = dc.parent_object_id
       AND c.column_id = dc.parent_column_id
    INNER JOIN sys.tables t
        ON t.object_id = c.object_id
    INNER JOIN sys.schemas s
        ON s.schema_id = t.schema_id
    WHERE s.name = N'dbo'
      AND t.name = N'Bookings'
      AND c.name = N'PromotionCode';

    IF @ConstraintName IS NOT NULL
        EXEC(N'ALTER TABLE [dbo].[Bookings] DROP CONSTRAINT ' + QUOTENAME(@ConstraintName));

    ALTER TABLE [dbo].[Bookings] DROP COLUMN [PromotionCode];
END;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "Bookings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
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
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MaximumDiscount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MinimumRentalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PromotionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StartAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsageLimit = table.Column<int>(type: "int", nullable: true),
                    UsedCount = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
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
}
