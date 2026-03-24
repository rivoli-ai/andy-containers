using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeAssistantAndApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeAssistant",
                table: "Templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodeAssistant",
                table: "Containers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiKeyCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    EncryptedValue = table.Column<string>(type: "text", nullable: false),
                    EnvVarName = table.Column<string>(type: "text", nullable: false),
                    MaskedValue = table.Column<string>(type: "text", nullable: true),
                    IsValid = table.Column<bool>(type: "boolean", nullable: false),
                    LastValidatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangeHistory = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyCredentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_OwnerId",
                table: "ApiKeyCredentials",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCredentials_OwnerId_Provider_Label",
                table: "ApiKeyCredentials",
                columns: new[] { "OwnerId", "Provider", "Label" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyCredentials");

            migrationBuilder.DropColumn(
                name: "CodeAssistant",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "CodeAssistant",
                table: "Containers");
        }
    }
}
