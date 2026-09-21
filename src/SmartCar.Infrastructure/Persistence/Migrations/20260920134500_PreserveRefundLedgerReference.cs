using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations
{
    public partial class PreserveRefundLedgerReference : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LedgerReference",
                table: "Payments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [Payments]
                SET [LedgerReference] = [TransactionCode]
                WHERE [Type] = N'Refund'
                  AND [Method] = N'CompensationRefund'
                  AND [TransactionCode] LIKE N'OVERDUE-FUNDED-%';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LedgerReference",
                table: "Payments");
        }
    }
}
