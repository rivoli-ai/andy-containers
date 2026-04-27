using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendlyNameAndOsLabelToContainers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FriendlyName",
                table: "Containers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsLabel",
                table: "Containers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FriendlyName",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "OsLabel",
                table: "Containers");
        }
    }
}
