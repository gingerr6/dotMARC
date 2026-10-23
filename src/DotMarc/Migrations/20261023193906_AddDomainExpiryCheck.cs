using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainExpiryCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DomainExpiryAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DomainExpiresUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DomainExpiryCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainExpiryDetail",
                table: "Domains",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainExpiryStatus",
                table: "Domains",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<string>(
                name: "DomainRegistrar",
                table: "Domains",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "NotificationSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "DomainExpiryAlertMode",
                value: "WhenItBreaks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DomainExpiryAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "DomainExpiresUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DomainExpiryCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DomainExpiryDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DomainExpiryStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DomainRegistrar",
                table: "Domains");

            migrationBuilder.UpdateData(
                table: "BackupSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "NotifyRecipients",
                value: new List<string>());
        }
    }
}
