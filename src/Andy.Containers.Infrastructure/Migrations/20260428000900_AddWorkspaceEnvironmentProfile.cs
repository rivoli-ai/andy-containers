using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceEnvironmentProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentProfileId",
                table: "Workspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_EnvironmentProfileId",
                table: "Workspaces",
                column: "EnvironmentProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Workspaces_EnvironmentProfiles_EnvironmentProfileId",
                table: "Workspaces",
                column: "EnvironmentProfileId",
                principalTable: "EnvironmentProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Workspaces_EnvironmentProfiles_EnvironmentProfileId",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_EnvironmentProfileId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "EnvironmentProfileId",
                table: "Workspaces");
        }
    }
}
