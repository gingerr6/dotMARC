using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddNinjaOneSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NinjaOneSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Instance = table.Column<string>(type: "text", nullable: true),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    ClientSecretConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    RefreshTokenConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectedBy = table.Column<string>(type: "text", nullable: true),
                    ConnectedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRefreshUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectionLostUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectionLostReason = table.Column<string>(type: "text", nullable: true),
                    TicketFormId = table.Column<int>(type: "integer", nullable: true),
                    TicketFormName = table.Column<string>(type: "text", nullable: true),
                    NewStatusId = table.Column<int>(type: "integer", nullable: true),
                    NewStatusName = table.Column<string>(type: "text", nullable: true),
                    TicketType = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NinjaOneSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "NinjaOneSettings",
                columns: new[] { "Id", "ClientId", "ClientSecretConfigured", "ConnectedBy", "ConnectedUtc", "ConnectionLostReason", "ConnectionLostUtc", "Enabled", "Instance", "LastRefreshUtc", "NewStatusId", "NewStatusName", "Priority", "RefreshTokenConfigured", "Severity", "TicketFormId", "TicketFormName", "TicketType" },
                values: new object[] { 1, null, false, null, null, null, null, false, null, null, 1000, "New", "NONE", false, "NONE", null, null, "PROBLEM" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NinjaOneSettings");
        }
    }
}
