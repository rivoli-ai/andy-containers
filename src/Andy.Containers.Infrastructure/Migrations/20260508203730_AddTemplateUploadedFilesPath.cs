using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateUploadedFilesPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UploadedFilesPath",
                table: "Templates",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadedFilesPath",
                table: "Templates");
        }
    }
}
