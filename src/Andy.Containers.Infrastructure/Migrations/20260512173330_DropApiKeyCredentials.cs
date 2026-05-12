using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <summary>
    /// rivoli-ai/conductor#946 (M1.5.4). Drop the legacy
    /// <c>ApiKeyCredentials</c> table. Provider keys now live in
    /// andy-settings under <c>andy.models.providers.&lt;slug&gt;.apiKey</c>
    /// and reach containers via the andy-models proxy
    /// (rivoli-ai/conductor#944). Operators with rows in this table
    /// must export them and re-create them in andy-settings before
    /// deploying this migration — see
    /// <c>docs/migrations/api-keys-to-settings.md</c>.
    /// </summary>
    /// <inheritdoc />
    public partial class DropApiKeyCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyCredentials");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror of the original table from
            // 20260210181512_InitialCreate so a downgrade restores
            // schema (data is permanently gone). Operators should
            // not downgrade past this migration on a production DB
            // without first reseeding from an andy-settings export.
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
                    ChangeHistory = table.Column<string>(type: "jsonb", nullable: true),
                    BaseUrl = table.Column<string>(type: "text", nullable: true),
                    ModelName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_ApiKeyCredentials", x => x.Id));

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
    }
}
