using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UxIdentityHoldHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReturnerFaceCaptureMethod",
                table: "VehicleReturns",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReturnerFaceCapturedAt",
                table: "VehicleReturns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnerFaceImagePath",
                table: "VehicleReturns",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiverFaceCaptureMethod",
                table: "VehicleHandovers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceiverFaceCapturedAt",
                table: "VehicleHandovers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiverFaceImagePath",
                table: "VehicleHandovers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StaffReviewedAt",
                table: "Bookings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaffReviewedByStaffId",
                table: "Bookings",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityFaceCaptureMethod",
                table: "AspNetUsers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IdentityFaceCapturedAt",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityFaceImagePath",
                table: "AspNetUsers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingHoldEvents",
                columns: table => new
                {
                    BookingHoldEventId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BookingId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsWaived = table.Column<bool>(type: "bit", nullable: false),
                    WaivedByAdminId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    WaivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WaiveReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingHoldEvents", x => x.BookingHoldEventId);
                    table.ForeignKey(
                        name: "FK_BookingHoldEvents_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "BookingId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityCaptureSessions",
                columns: table => new
                {
                    IdentityCaptureSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TargetCustomerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    BookingId = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ImagePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CaptureMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FallbackReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityCaptureSessions", x => x.IdentityCaptureSessionId);
                    table.ForeignKey(
                        name: "FK_IdentityCaptureSessions_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "BookingId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingHoldEvents_BookingId",
                table: "BookingHoldEvents",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingHoldEvents_CustomerId_OccurredAt",
                table: "BookingHoldEvents",
                columns: new[] { "CustomerId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingHoldEvents_VehicleId_OccurredAt",
                table: "BookingHoldEvents",
                columns: new[] { "VehicleId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityCaptureSessions_BookingId",
                table: "IdentityCaptureSessions",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityCaptureSessions_TargetCustomerId_Purpose_ExpiresAt",
                table: "IdentityCaptureSessions",
                columns: new[] { "TargetCustomerId", "Purpose", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityCaptureSessions_TokenHash",
                table: "IdentityCaptureSessions",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingHoldEvents");

            migrationBuilder.DropTable(
                name: "IdentityCaptureSessions");

            migrationBuilder.DropColumn(
                name: "ReturnerFaceCaptureMethod",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "ReturnerFaceCapturedAt",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "ReturnerFaceImagePath",
                table: "VehicleReturns");

            migrationBuilder.DropColumn(
                name: "ReceiverFaceCaptureMethod",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "ReceiverFaceCapturedAt",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "ReceiverFaceImagePath",
                table: "VehicleHandovers");

            migrationBuilder.DropColumn(
                name: "StaffReviewedAt",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "StaffReviewedByStaffId",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "IdentityFaceCaptureMethod",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IdentityFaceCapturedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IdentityFaceImagePath",
                table: "AspNetUsers");
        }
    }
}
