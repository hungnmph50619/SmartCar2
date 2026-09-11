using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260911143000_AdminStaffManagement")]
public sealed class AdminStaffManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.AspNetUsers', N'EmployeeCode') IS NULL
                ALTER TABLE [dbo].[AspNetUsers] ADD [EmployeeCode] nvarchar(30) NULL;

            IF COL_LENGTH(N'dbo.AspNetUsers', N'CitizenIdNumber') IS NULL
                ALTER TABLE [dbo].[AspNetUsers] ADD [CitizenIdNumber] nvarchar(20) NULL;

            IF COL_LENGTH(N'dbo.AspNetUsers', N'CreatedByUserId') IS NULL
                ALTER TABLE [dbo].[AspNetUsers] ADD [CreatedByUserId] nvarchar(450) NULL;

            IF COL_LENGTH(N'dbo.AspNetUsers', N'VerifiedByUserId') IS NULL
                ALTER TABLE [dbo].[AspNetUsers] ADD [VerifiedByUserId] nvarchar(450) NULL;

            IF COL_LENGTH(N'dbo.AspNetUsers', N'VerifiedAt') IS NULL
                ALTER TABLE [dbo].[AspNetUsers] ADD [VerifiedAt] datetime2 NULL;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_AspNetUsers_EmployeeCode'
                  AND [object_id] = OBJECT_ID(N'dbo.AspNetUsers'))
            BEGIN
                CREATE UNIQUE INDEX [IX_AspNetUsers_EmployeeCode]
                    ON [dbo].[AspNetUsers] ([EmployeeCode])
                    WHERE [EmployeeCode] IS NOT NULL;
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_AspNetUsers_CitizenIdNumber'
                  AND [object_id] = OBJECT_ID(N'dbo.AspNetUsers'))
            BEGIN
                CREATE UNIQUE INDEX [IX_AspNetUsers_CitizenIdNumber]
                    ON [dbo].[AspNetUsers] ([CitizenIdNumber])
                    WHERE [CitizenIdNumber] IS NOT NULL;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_AspNetUsers_EmployeeCode'
                  AND [object_id] = OBJECT_ID(N'dbo.AspNetUsers'))
                DROP INDEX [IX_AspNetUsers_EmployeeCode] ON [dbo].[AspNetUsers];

            IF EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_AspNetUsers_CitizenIdNumber'
                  AND [object_id] = OBJECT_ID(N'dbo.AspNetUsers'))
                DROP INDEX [IX_AspNetUsers_CitizenIdNumber] ON [dbo].[AspNetUsers];

            IF COL_LENGTH(N'dbo.AspNetUsers', N'VerifiedAt') IS NOT NULL
                ALTER TABLE [dbo].[AspNetUsers] DROP COLUMN [VerifiedAt];

            IF COL_LENGTH(N'dbo.AspNetUsers', N'VerifiedByUserId') IS NOT NULL
                ALTER TABLE [dbo].[AspNetUsers] DROP COLUMN [VerifiedByUserId];

            IF COL_LENGTH(N'dbo.AspNetUsers', N'CreatedByUserId') IS NOT NULL
                ALTER TABLE [dbo].[AspNetUsers] DROP COLUMN [CreatedByUserId];

            IF COL_LENGTH(N'dbo.AspNetUsers', N'CitizenIdNumber') IS NOT NULL
                ALTER TABLE [dbo].[AspNetUsers] DROP COLUMN [CitizenIdNumber];

            IF COL_LENGTH(N'dbo.AspNetUsers', N'EmployeeCode') IS NOT NULL
                ALTER TABLE [dbo].[AspNetUsers] DROP COLUMN [EmployeeCode];
            """);
    }
}
