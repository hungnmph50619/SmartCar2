using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RentalDeliveryDepositAndHandoverPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DamageCompensationTerms",
                table: "VehicleHandovers",
                type: "nvarchar(1500)",
                maxLength: 1500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "ExcessKmFeePerKm",
                table: "VehicleHandovers",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "IncludedKilometers",
                table: "VehicleHandovers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "LateReturnFeeMultiplier",
                table: "VehicleHandovers",
                type: "decimal(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "PenaltyPolicyAccepted",
                table: "VehicleHandovers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TrafficFineTerms",
                table: "VehicleHandovers",
                type: "nvarchar(1500)",
                maxLength: 1500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryAddress",
                table: "Bookings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryLatitude",
                table: "Bookings",
                type: "decimal(10,7)",
                precision: 10,
                scale: 7,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryLongitude",
                table: "Bookings",
                type: "decimal(10,7)",
                precision: 10,
                scale: 7,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DepositAmount",
                table: "Bookings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PickupMethod",
                table: "Bookings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DamageCompensationTerms",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "ExcessKmFeePerKm",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "IncludedKilometers",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "LateReturnFeeMultiplier",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "PenaltyPolicyAccepted",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "TrafficFineTerms",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "DeliveryAddress",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DeliveryLatitude",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DeliveryLongitude",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "DepositAmount",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PickupMethod",
                table: "Bookings");
        }
    }
}
