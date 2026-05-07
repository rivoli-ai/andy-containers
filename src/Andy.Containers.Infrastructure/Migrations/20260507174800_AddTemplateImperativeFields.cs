using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateImperativeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntryPoint",
                table: "Templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Extends",
                table: "Templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Files",
                table: "Templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Install",
                table: "Templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Markers",
                table: "Templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Packages",
                table: "Templates",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryPoint",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "Extends",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "Files",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "Install",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "Markers",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "Packages",
                table: "Templates");
        }
    }
}
