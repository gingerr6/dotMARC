using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddBimiCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BimiCertificateExpiresUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BimiCheckDetail",
                table: "Domains",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BimiCheckStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BimiCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BimiLogoUrl",
                table: "Domains",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BimiCertificateExpiresUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "BimiCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "BimiCheckStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "BimiCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "BimiLogoUrl",
                table: "Domains");
        }
    }
}
