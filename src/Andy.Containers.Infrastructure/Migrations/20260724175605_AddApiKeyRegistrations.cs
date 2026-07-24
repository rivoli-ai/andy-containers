using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeyAuditRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyAuditRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeyRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SecretDefinitionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MaskedValue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsValid = table.Column<bool>(type: "boolean", nullable: true),
                    LastValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyRegistrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyAuditRecords_OwnerId_KeyId_OccurredAt",
                table: "ApiKeyAuditRecords",
                columns: new[] { "OwnerId", "KeyId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyRegistrations_OwnerId",
                table: "ApiKeyRegistrations",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyRegistrations_OwnerId_Provider",
                table: "ApiKeyRegistrations",
                columns: new[] { "OwnerId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyAuditRecords");

            migrationBuilder.DropTable(
                name: "ApiKeyRegistrations");
        }
    }
}
