using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiRepoAndCloneMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GitRepositories",
                table: "Workspaces",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloneMetadata",
                table: "ContainerGitRepositories",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GitRepositories",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "CloneMetadata",
                table: "ContainerGitRepositories");
        }
    }
}
