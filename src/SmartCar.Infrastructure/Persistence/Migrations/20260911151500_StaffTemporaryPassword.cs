using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartCar.Infrastructure.Persistence;

#nullable disable

namespace SmartCar.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260911151500_StaffTemporaryPassword")]
public sealed class StaffTemporaryPassword : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.AspNetUsers', N'MustChangePassword') IS NULL
            BEGIN
                ALTER TABLE [dbo].[AspNetUsers]
                    ADD [MustChangePassword] bit NOT NULL
                    CONSTRAINT [DF_AspNetUsers_MustChangePassword] DEFAULT CAST(0 AS bit);
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            IF COL_LENGTH(N'dbo.AspNetUsers', N'MustChangePassword') IS NOT NULL
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM sys.default_constraints dc
                    INNER JOIN sys.columns c
                        ON c.default_object_id = dc.object_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.AspNetUsers')
                      AND c.name = N'MustChangePassword')
                BEGIN
                    DECLARE @constraintName sysname;
                    SELECT @constraintName = dc.name
                    FROM sys.default_constraints dc
                    INNER JOIN sys.columns c
                        ON c.default_object_id = dc.object_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.AspNetUsers')
                      AND c.name = N'MustChangePassword';
                    EXEC(N'ALTER TABLE [dbo].[AspNetUsers] DROP CONSTRAINT [' + @constraintName + N']');
                END;

                ALTER TABLE [dbo].[AspNetUsers] DROP COLUMN [MustChangePassword];
            END;
            """);
    }
}
