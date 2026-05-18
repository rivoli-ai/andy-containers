using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRunOutputArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutputArtifacts",
                table: "Runs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutputArtifacts",
                table: "Runs");
        }
    }
}
