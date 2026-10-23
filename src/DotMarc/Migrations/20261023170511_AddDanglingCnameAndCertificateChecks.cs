using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDanglingCnameAndCertificateChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CertificateAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<string>(
                name: "DanglingCnameAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificateCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateDetail",
                table: "Domains",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificateExpiresUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateStatus",
                table: "Domains",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DanglingCnameCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DanglingCnameDetail",
                table: "Domains",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DanglingCnameStatus",
                table: "Domains",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.UpdateData(
                table: "NotificationSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CertificateAlertMode", "DanglingCnameAlertMode" },
                values: new object[] { "WhenItBreaks", "WhenItBreaks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CertificateAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "DanglingCnameAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "CertificateCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "CertificateDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "CertificateExpiresUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "CertificateStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DanglingCnameCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DanglingCnameDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DanglingCnameStatus",
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
