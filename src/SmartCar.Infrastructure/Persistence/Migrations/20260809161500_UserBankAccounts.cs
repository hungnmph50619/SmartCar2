using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260809161500_UserBankAccounts")]
public sealed class UserBankAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserBankAccounts",
            columns: table => new
            {
                UserBankAccountId = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                BankCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                BankName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                AccountNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                AccountHolderName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                IsDefault = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserBankAccounts", x => x.UserBankAccountId);
                table.ForeignKey(
                    name: "FK_UserBankAccounts_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserBankAccounts_UserId_BankCode_AccountNumber",
            table: "UserBankAccounts",
            columns: new[] { "UserId", "BankCode", "AccountNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserBankAccounts_Default",
            table: "UserBankAccounts",
            column: "UserId",
            unique: true,
            filter: "[IsDefault] = 1 AND [IsActive] = 1");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserBankAccounts");
    }
}
