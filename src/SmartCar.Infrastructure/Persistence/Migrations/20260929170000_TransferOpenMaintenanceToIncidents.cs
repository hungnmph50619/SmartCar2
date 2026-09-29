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
        // Preserve each open record and its historical costs. Its corresponding
        // incident keeps the vehicle blocked until Admin closes that case.
        migrationBuilder.Sql("""
            INSERT INTO VehicleIncidents
                (VehicleId, BookingId, IncidentType, Status, OccurredAt,
                 Description, EstimatedCost, ActualCost, FineAmount,
                 CustomerLiabilityAmount, Notes, CreatedAt)
            SELECT m.VehicleId, NULL, 'Breakdown', 'Open', m.StartDate,
                   CONCAT(N'Sự cố xe trước đây: ', m.Content), m.Cost, 0, 0, 0,
                   CONCAT('LEGACY-MAINTENANCE-ID:', m.MaintenanceRecordId),
                   GETUTCDATE()
            FROM MaintenanceRecords AS m
            WHERE m.Status = 'InProgress';

            UPDATE v
            SET Status = 'Inactive'
            FROM Vehicles AS v
            WHERE v.Status = 'Maintenance'
              AND EXISTS (
                  SELECT 1 FROM MaintenanceRecords AS m
                  WHERE m.VehicleId = v.VehicleId
                    AND m.Status = 'InProgress');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A resolved incident may now have real follow-up data; reversing this
        // transfer would reopen old vehicle holds or remove those records.
    }
}
