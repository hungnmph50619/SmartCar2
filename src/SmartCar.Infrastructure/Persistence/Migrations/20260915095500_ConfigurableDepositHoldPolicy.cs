using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260915095500_ConfigurableDepositHoldPolicy")]
public sealed class ConfigurableDepositHoldPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'dbo.BusinessSettings', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[BusinessSettings]
                (
                    [BusinessSettingId] int NOT NULL,
                    [DepositHoldDays] int NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL,
                    [UpdatedByUserId] nvarchar(450) NULL,
                    CONSTRAINT [PK_BusinessSettings] PRIMARY KEY ([BusinessSettingId]),
                    CONSTRAINT [CK_BusinessSettings_DepositHoldDays]
                        CHECK ([DepositHoldDays] >= 0 AND [DepositHoldDays] <= 90)
                );
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM [dbo].[BusinessSettings]
                WHERE [BusinessSettingId] = 1)
            BEGIN
                INSERT INTO [dbo].[BusinessSettings]
                    ([BusinessSettingId], [DepositHoldDays], [UpdatedAt], [UpdatedByUserId])
                VALUES
                    (1, 15, SYSUTCDATETIME(), NULL);
            END;

            IF COL_LENGTH(N'dbo.Bookings', N'DepositHoldDaysApplied') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Bookings]
                ADD [DepositHoldDaysApplied] int NOT NULL
                    CONSTRAINT [DF_Bookings_DepositHoldDaysApplied] DEFAULT (15) WITH VALUES;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE [name] = N'CK_Bookings_DepositHoldDaysApplied')
            BEGIN
                ALTER TABLE [dbo].[Bookings] WITH CHECK
                ADD CONSTRAINT [CK_Bookings_DepositHoldDaysApplied]
                    CHECK ([DepositHoldDaysApplied] >= 0 AND [DepositHoldDaysApplied] <= 90);
            END;
            """);

        // Chặn mọi đường cập nhật trực tiếp trạng thái hoàn cọc nếu:
        // - chưa đủ thời gian giữ cọc của chính booking; hoặc
        // - booking còn khoản phạt/vi phạm chưa thanh toán xong.
        // ReturnedAt đang được lưu theo giờ nghiệp vụ Việt Nam nên so sánh với UTC+7 độc lập timezone server SQL.
        migrationBuilder.Sql("""
            CREATE OR ALTER TRIGGER [dbo].[TR_Payments_BlockEarlyDepositRefund]
            ON [dbo].[Payments]
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;

                IF EXISTS
                (
                    SELECT 1
                    FROM inserted i
                    INNER JOIN deleted d ON d.[PaymentId] = i.[PaymentId]
                    INNER JOIN [dbo].[Bookings] b ON b.[BookingId] = i.[BookingId]
                    INNER JOIN [dbo].[VehicleReturns] vr ON vr.[BookingId] = b.[BookingId]
                    WHERE i.[Type] = N'Refund'
                      AND i.[Method] = N'Hoàn cọc'
                      AND i.[Status] IN (N'RefundApproved', N'Refunded')
                      AND ISNULL(d.[Status], N'') <> ISNULL(i.[Status], N'')
                      AND
                      (
                          DATEADD(DAY, b.[DepositHoldDaysApplied], vr.[ReturnedAt])
                              > DATEADD(HOUR, 7, SYSUTCDATETIME())
                          OR EXISTS
                          (
                              SELECT 1
                              FROM [dbo].[Payments] tf
                              WHERE tf.[BookingId] = b.[BookingId]
                                AND tf.[Type] = N'TrafficFine'
                                AND tf.[Status] IN (N'Pending', N'AwaitingConfirmation', N'Failed')
                                AND tf.[Amount] > 0
                          )
                      )
                )
                BEGIN
                    THROW 51015, N'Chưa đủ điều kiện hoàn cọc: còn thời gian giữ cọc hoặc khoản phạt/vi phạm chưa xử lý.', 1;
                END;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'dbo.TR_Payments_BlockEarlyDepositRefund', N'TR') IS NOT NULL
                DROP TRIGGER [dbo].[TR_Payments_BlockEarlyDepositRefund];

            IF EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE [name] = N'CK_Bookings_DepositHoldDaysApplied')
            BEGIN
                ALTER TABLE [dbo].[Bookings]
                    DROP CONSTRAINT [CK_Bookings_DepositHoldDaysApplied];
            END;

            IF EXISTS (
                SELECT 1
                FROM sys.default_constraints
                WHERE [name] = N'DF_Bookings_DepositHoldDaysApplied')
            BEGIN
                ALTER TABLE [dbo].[Bookings]
                    DROP CONSTRAINT [DF_Bookings_DepositHoldDaysApplied];
            END;

            IF COL_LENGTH(N'dbo.Bookings', N'DepositHoldDaysApplied') IS NOT NULL
                ALTER TABLE [dbo].[Bookings] DROP COLUMN [DepositHoldDaysApplied];

            IF OBJECT_ID(N'dbo.BusinessSettings', N'U') IS NOT NULL
                DROP TABLE [dbo].[BusinessSettings];
            """);
    }
}
