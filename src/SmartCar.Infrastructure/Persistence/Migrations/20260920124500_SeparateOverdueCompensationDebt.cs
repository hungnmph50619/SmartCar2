using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Tách khoản bồi thường quá hạn còn phải thu khỏi ledger phụ phí trả xe.
    /// Đây là data-only migration: Payment.Type đang lưu dạng string nên không đổi schema.
    /// </summary>
    public partial class SeparateOverdueCompensationDebt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE [Payments]
                SET [Type] = N'OverdueCompensationDebt'
                WHERE [Type] = N'AdditionalCharge'
                  AND [Method] <> N'DepositDeduction'
                  AND [TransactionCode] LIKE N'OVERDUE-DEBT-%';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE [Payments]
                SET [Type] = N'AdditionalCharge'
                WHERE [Type] = N'OverdueCompensationDebt'
                  AND [TransactionCode] LIKE N'OVERDUE-DEBT-%';
                """);
        }
    }
}
