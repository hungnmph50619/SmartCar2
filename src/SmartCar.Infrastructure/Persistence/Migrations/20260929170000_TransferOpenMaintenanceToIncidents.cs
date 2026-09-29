using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260929170000_TransferOpenMaintenanceToIncidents")]
public sealed class TransferOpenMaintenanceToIncidents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve existing holds when the maintenance screen is removed. An open
        // incident becomes the single reason a vehicle is blocked from bookings.
        migrationBuilder.Sql("""
            WITH LatestOpenRecord AS (
                SELECT m.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY m.VehicleId
                           ORDER BY m.StartDate DESC, m.MaintenanceRecordId DESC) AS RowNumber
                FROM MaintenanceRecords AS m
                WHERE m.Status = 'InProgress'
            )
            INSERT INTO VehicleIncidents
                (VehicleId, BookingId, IncidentType, Status, OccurredAt,
                 Description, EstimatedCost, ActualCost, FineAmount,
                 CustomerLiabilityAmount, CreatedAt)
            SELECT m.VehicleId, NULL, 'Breakdown', 'Open', m.StartDate,
                   CONCAT(N'Sự cố xe trước đây: ', m.Content), m.Cost, 0, 0, 0,
                   GETUTCDATE()
            FROM LatestOpenRecord AS m
            WHERE m.RowNumber = 1
              AND NOT EXISTS (
                  SELECT 1 FROM VehicleIncidents AS i
                  WHERE i.VehicleId = m.VehicleId
                    AND i.IncidentType <> 'TrafficFine'
                    AND i.Status IN ('Open', 'Investigating'));

            UPDATE MaintenanceRecords
            SET Status = 'Cancelled'
            WHERE Status = 'InProgress';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A resolved incident may now have real follow-up data; reversing this
        // transfer would reopen old vehicle holds or remove those records.
    }
}
