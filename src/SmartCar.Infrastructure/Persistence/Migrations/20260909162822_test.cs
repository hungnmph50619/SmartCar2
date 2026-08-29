using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class test : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CustomerIdentityVerified",
                table: "VehicleReturns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerifiedAt",
                table: "VehicleReturns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityVerifiedByStaffId",
                table: "VehicleReturns",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SignedDocumentVerified",
                table: "VehicleReturns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedDocumentVerifiedAt",
                table: "VehicleReturns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedDocumentVerifiedByStaffId",
                table: "VehicleReturns",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CustomerIdentityVerified",
                table: "VehicleHandovers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityVerifiedAt",
                table: "VehicleHandovers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityVerifiedByStaffId",
                table: "VehicleHandovers",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SignedDocumentVerified",
                table: "VehicleHandovers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedDocumentVerifiedAt",
                table: "VehicleHandovers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedDocumentVerifiedByStaffId",
                table: "VehicleHandovers",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerIdentityVerified",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "IdentityVerifiedAt",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "IdentityVerifiedByStaffId",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "SignedDocumentVerified",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "SignedDocumentVerifiedAt",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "SignedDocumentVerifiedByStaffId",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "CustomerIdentityVerified",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "IdentityVerifiedAt",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "IdentityVerifiedByStaffId",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "SignedDocumentVerified",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "SignedDocumentVerifiedAt",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "SignedDocumentVerifiedByStaffId",
                table: "VehicleHandovers");
        }
    }
}
