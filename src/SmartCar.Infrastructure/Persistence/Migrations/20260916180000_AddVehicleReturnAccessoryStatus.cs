using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleReturnAccessoryStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessoryStatus",
                table: "VehicleReturns",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [VehicleReturns]
                SET [AccessoryStatus] = LTRIM(RTRIM(
                    CASE
                        WHEN CHARINDEX(N' | Ghi chú: ', [Notes]) > 0 THEN
                            SUBSTRING(
                                [Notes],
                                LEN(N'Phụ kiện khi trả:') + 1,
                                CHARINDEX(N' | Ghi chú: ', [Notes]) - LEN(N'Phụ kiện khi trả:') - 1)
                        ELSE
                            SUBSTRING(
                                [Notes],
                                LEN(N'Phụ kiện khi trả:') + 1,
                                LEN([Notes]))
                    END))
                WHERE [AccessoryStatus] IS NULL
                  AND [Notes] LIKE N'Phụ kiện khi trả:%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessoryStatus",
                table: "VehicleReturns");
        }
    }
}
