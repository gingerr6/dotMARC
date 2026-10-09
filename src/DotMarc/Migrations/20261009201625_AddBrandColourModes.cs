using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandColourModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DarkPrimaryColour",
                table: "GroupBrandings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DarkSecondaryColour",
                table: "GroupBrandings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DarkBackgroundColour",
                table: "BrandingSettings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DarkPrimaryColour",
                table: "BrandingSettings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DarkSecondaryColour",
                table: "BrandingSettings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DarkSurfaceColour",
                table: "BrandingSettings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LightBackgroundColour",
                table: "BrandingSettings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LightSurfaceColour",
                table: "BrandingSettings",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "BrandingSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "DarkBackgroundColour", "DarkPrimaryColour", "DarkSecondaryColour", "DarkSurfaceColour", "LightBackgroundColour", "LightSurfaceColour" },
                values: new object[] { null, null, null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DarkPrimaryColour",
                table: "GroupBrandings");

            migrationBuilder.DropColumn(
                name: "DarkSecondaryColour",
                table: "GroupBrandings");

            migrationBuilder.DropColumn(
                name: "DarkBackgroundColour",
                table: "BrandingSettings");

            migrationBuilder.DropColumn(
                name: "DarkPrimaryColour",
                table: "BrandingSettings");

            migrationBuilder.DropColumn(
                name: "DarkSecondaryColour",
                table: "BrandingSettings");

            migrationBuilder.DropColumn(
                name: "DarkSurfaceColour",
                table: "BrandingSettings");

            migrationBuilder.DropColumn(
                name: "LightBackgroundColour",
                table: "BrandingSettings");

            migrationBuilder.DropColumn(
                name: "LightSurfaceColour",
                table: "BrandingSettings");
        }
    }
}
