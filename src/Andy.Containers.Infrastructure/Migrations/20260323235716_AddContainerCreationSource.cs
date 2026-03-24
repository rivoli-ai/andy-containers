using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerCreationSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Images_TemplateId",
                table: "Images");

            migrationBuilder.AddColumn<string>(
                name: "GitRepositories",
                table: "Templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Images",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Images",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                table: "Images",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ClientInfo",
                table: "Containers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreationSource",
                table: "Containers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ContainerGitRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Branch = table.Column<string>(type: "text", nullable: true),
                    TargetPath = table.Column<string>(type: "text", nullable: false),
                    CredentialRef = table.Column<string>(type: "text", nullable: true),
                    CloneDepth = table.Column<int>(type: "integer", nullable: true),
                    Submodules = table.Column<bool>(type: "boolean", nullable: false),
                    IsFromTemplate = table.Column<bool>(type: "boolean", nullable: false),
                    CloneStatus = table.Column<int>(type: "integer", nullable: false),
                    CloneError = table.Column<string>(type: "text", nullable: true),
                    CloneStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CloneCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerGitRepositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContainerGitRepositories_Containers_ContainerId",
                        column: x => x.ContainerId,
                        principalTable: "Containers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    GitHost = table.Column<string>(type: "text", nullable: true),
                    CredentialType = table.Column<int>(type: "integer", nullable: false),
                    EncryptedToken = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitCredentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Images_OrganizationId",
                table: "Images",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Images_TemplateId_OrganizationId",
                table: "Images",
                columns: new[] { "TemplateId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerGitRepositories_CloneStatus",
                table: "ContainerGitRepositories",
                column: "CloneStatus");

            migrationBuilder.CreateIndex(
                name: "IX_ContainerGitRepositories_ContainerId",
                table: "ContainerGitRepositories",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_GitCredentials_OwnerId",
                table: "GitCredentials",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_GitCredentials_OwnerId_Label",
                table: "GitCredentials",
                columns: new[] { "OwnerId", "Label" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerGitRepositories");

            migrationBuilder.DropTable(
                name: "GitCredentials");

            migrationBuilder.DropIndex(
                name: "IX_Images_OrganizationId",
                table: "Images");

            migrationBuilder.DropIndex(
                name: "IX_Images_TemplateId_OrganizationId",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "GitRepositories",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "ClientInfo",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "CreationSource",
                table: "Containers");

            migrationBuilder.CreateIndex(
                name: "IX_Images_TemplateId",
                table: "Images",
                column: "TemplateId");
        }
    }
}
