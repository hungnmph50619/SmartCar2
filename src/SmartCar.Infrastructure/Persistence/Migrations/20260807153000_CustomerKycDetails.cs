using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260807153000_CustomerKycDetails")]
public sealed class CustomerKycDetails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FullNameOnDocument",
            table: "CustomerDocuments",
            type: "nvarchar(150)",
            maxLength: 150,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "DateOfBirth",
            table: "CustomerDocuments",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Gender",
            table: "CustomerDocuments",
            type: "nvarchar(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "IssuedDate",
            table: "CustomerDocuments",
            type: "datetime2",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PermanentAddress",
            table: "CustomerDocuments",
            type: "nvarchar(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LicenseClass",
            table: "CustomerDocuments",
            type: "nvarchar(20)",
            maxLength: 20,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "FullNameOnDocument", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "DateOfBirth", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "Gender", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "IssuedDate", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "PermanentAddress", table: "CustomerDocuments");
        migrationBuilder.DropColumn(name: "LicenseClass", table: "CustomerDocuments");
    }
}
