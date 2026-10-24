using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddLookalikeAndBlocklistChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlocklistAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<string>(
                name: "LookalikeAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BlocklistCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlocklistDetail",
                table: "Domains",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlocklistStatus",
                table: "Domains",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LookalikeCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LookalikeDetail",
                table: "Domains",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LookalikeStatus",
                table: "Domains",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.CreateTable(
                name: "LookalikeDomains",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DomainId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HasMx = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Dismissed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookalikeDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LookalikeDomains_Domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "Domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "NotificationSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "BlocklistAlertMode", "LookalikeAlertMode" },
                values: new object[] { "WhenItBreaks", "WhenItBreaks" });

            migrationBuilder.CreateIndex(
                name: "IX_LookalikeDomains_DomainId_Name",
                table: "LookalikeDomains",
                columns: new[] { "DomainId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LookalikeDomains");

            migrationBuilder.DropColumn(
                name: "BlocklistAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "LookalikeAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "BlocklistCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "BlocklistDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "BlocklistStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "LookalikeCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "LookalikeDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "LookalikeStatus",
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
