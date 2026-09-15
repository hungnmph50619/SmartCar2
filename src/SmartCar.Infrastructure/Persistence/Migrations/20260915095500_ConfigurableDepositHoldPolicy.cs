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
        // Tách từng bước thành command riêng để SQL Server không phải compile
        // cả block với object chưa tồn tại. Tất cả đều idempotent để chạy an toàn
        // trên DB local đã có dữ liệu hoặc DB sạch.
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
            """);

        migrationBuilder.Sql("""
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
            """);

        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.Bookings', N'DepositHoldDaysApplied') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Bookings]
                ADD [DepositHoldDaysApplied] int NOT NULL
                    CONSTRAINT [DF_Bookings_DepositHoldDaysApplied] DEFAULT (15) WITH VALUES;
            END;
            """);

        migrationBuilder.Sql("""
            IF NOT EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE [name] = N'CK_Bookings_DepositHoldDaysApplied'
                  AND [parent_object_id] = OBJECT_ID(N'dbo.Bookings'))
            BEGIN
                ALTER TABLE [dbo].[Bookings] WITH CHECK
                ADD CONSTRAINT [CK_Bookings_DepositHoldDaysApplied]
                    CHECK ([DepositHoldDaysApplied] >= 0 AND [DepositHoldDaysApplied] <= 90);
            END;
            """);

        // Điều kiện hoàn cọc được chặn ở AdminPaymentsController và chỉ Staff
        // được thực hiện giao dịch sau khi Admin duyệt. Không dùng SQL trigger ở đây
        // để tránh làm migration startup thất bại trên các bản SQL Server/LocalDB khác nhau.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS (
                SELECT 1
                FROM sys.check_constraints
                WHERE [name] = N'CK_Bookings_DepositHoldDaysApplied'
                  AND [parent_object_id] = OBJECT_ID(N'dbo.Bookings'))
            BEGIN
                ALTER TABLE [dbo].[Bookings]
                    DROP CONSTRAINT [CK_Bookings_DepositHoldDaysApplied];
            END;
            """);

        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.Bookings', N'DepositHoldDaysApplied') IS NOT NULL
            BEGIN
                DECLARE @defaultConstraint sysname;
                SELECT @defaultConstraint = dc.[name]
                FROM sys.default_constraints dc
                INNER JOIN sys.columns c
                    ON c.[default_object_id] = dc.[object_id]
                WHERE dc.[parent_object_id] = OBJECT_ID(N'dbo.Bookings')
                  AND c.[name] = N'DepositHoldDaysApplied';

                IF @defaultConstraint IS NOT NULL
                    EXEC(N'ALTER TABLE [dbo].[Bookings] DROP CONSTRAINT [' + @defaultConstraint + N']');

                ALTER TABLE [dbo].[Bookings] DROP COLUMN [DepositHoldDaysApplied];
            END;
            """);

        migrationBuilder.Sql("""
            IF OBJECT_ID(N'dbo.BusinessSettings', N'U') IS NOT NULL
                DROP TABLE [dbo].[BusinessSettings];
            """);
    }
}
