using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastFailureEmailUtc",
                table: "BackupSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnSuccess",
                table: "BackupSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "NotifyRecipients",
                table: "BackupSettings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.UpdateData(
                table: "BackupSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "LastFailureEmailUtc", "NotifyOnSuccess", "NotifyRecipients" },
                values: new object[] { null, true, new List<string>() });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastFailureEmailUtc",
                table: "BackupSettings");

            migrationBuilder.DropColumn(
                name: "NotifyOnSuccess",
                table: "BackupSettings");

            migrationBuilder.DropColumn(
                name: "NotifyRecipients",
                table: "BackupSettings");
        }
    }
}
