using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260910170000_TrafficFineReceivables")]
public sealed class TrafficFineReceivables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Migration này có thể gặp database đã được chạy/sửa dở trước đó.
        // Dùng các guard ở SQL Server để tránh lỗi duplicate column/index/FK,
        // đồng thời vẫn tạo đủ schema còn thiếu trên cả DB sạch và DB hiện hữu.
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.Payments', N'VehicleIncidentId') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Payments] ADD [VehicleIncidentId] int NULL;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [name] = N'IX_Payments_VehicleIncidentId'
                  AND [object_id] = OBJECT_ID(N'dbo.Payments'))
            BEGIN
                CREATE INDEX [IX_Payments_VehicleIncidentId]
                    ON [dbo].[Payments] ([VehicleIncidentId]);
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE [name] = N'FK_Payments_VehicleIncidents_VehicleIncidentId'
                  AND [parent_object_id] = OBJECT_ID(N'dbo.Payments'))
            BEGIN
                ALTER TABLE [dbo].[Payments] WITH CHECK
                ADD CONSTRAINT [FK_Payments_VehicleIncidents_VehicleIncidentId]
                    FOREIGN KEY ([VehicleIncidentId])
                    REFERENCES [dbo].[VehicleIncidents] ([VehicleIncidentId])
                    ON DELETE NO ACTION;

                ALTER TABLE [dbo].[Payments]
                    CHECK CONSTRAINT [FK_Payments_VehicleIncidents_VehicleIncidentId];
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE [name] = N'FK_Payments_VehicleIncidents_VehicleIncidentId'
                  AND [parent_object_id] = OBJECT_ID(N'dbo.Payments'))
            BEGIN
                ALTER TABLE [dbo].[Payments]
                    DROP CONSTRAINT [FK_Payments_VehicleIncidents_VehicleIncidentId];
            END;

            IF EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [name] = N'IX_Payments_VehicleIncidentId'
                  AND [object_id] = OBJECT_ID(N'dbo.Payments'))
            BEGIN
                DROP INDEX [IX_Payments_VehicleIncidentId] ON [dbo].[Payments];
            END;

            IF COL_LENGTH(N'dbo.Payments', N'VehicleIncidentId') IS NOT NULL
            BEGIN
                ALTER TABLE [dbo].[Payments] DROP COLUMN [VehicleIncidentId];
            END;
            """);
    }
}
