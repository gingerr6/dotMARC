using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceSuggestionSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ApiKeyConfigured = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSuggestionSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceSuggestionsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SourceSuggestionSettings",
                columns: new[] { "Id", "ApiKeyConfigured", "Enabled", "Endpoint", "Model" },
                values: new object[] { 1, false, false, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserKey",
                table: "UserPreferences",
                column: "UserKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceSuggestionSettings");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.UpdateData(
                table: "BackupSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "NotifyRecipients",
                value: new List<string>());
        }
    }
}
