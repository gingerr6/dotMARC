using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class RemovePersonalSourceSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SourceSuggestions is no longer a permission. A stored name would fail to load, because the column is
            // parsed as the Permission enum. Removing it does not take Suggest away: every signed-in app user may ask.
            migrationBuilder.Sql("""
                UPDATE "Roles"
                SET "Permissions" = array_remove("Permissions", 'SourceSuggestions')
                WHERE 'SourceSuggestions' = ANY("Permissions");
                """);

            migrationBuilder.DropTable(
                name: "UserPreferences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceSuggestionsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UserKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserKey",
                table: "UserPreferences",
                column: "UserKey",
                unique: true);
        }
    }
}
