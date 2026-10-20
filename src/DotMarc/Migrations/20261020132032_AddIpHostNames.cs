using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddIpHostNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IpHostNames",
                columns: table => new
                {
                    Ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    HostName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    LookedUpUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpHostNames", x => x.Ip);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IpHostNames");
        }
    }
}
