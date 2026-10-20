using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Prefix = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EndpointUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AccessKeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SecretAccessKeyConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    PassphraseConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    RunHourUtc = table.Column<int>(type: "integer", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    LastRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastRunMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastSuccessUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastBackupKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LastBackupSize = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "BackupSettings",
                columns: new[] { "Id", "AccessKeyId", "Bucket", "Enabled", "EndpointUrl", "LastBackupKey", "LastBackupSize", "LastRunMessage", "LastRunSucceeded", "LastRunUtc", "LastSuccessUtc", "PassphraseConfigured", "Prefix", "Region", "RetentionDays", "RunHourUtc", "SecretAccessKeyConfigured" },
                values: new object[] { 1, null, null, false, null, null, null, null, null, null, null, false, "dotmarc-backups", null, 30, 3, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupSettings");
        }
    }
}
