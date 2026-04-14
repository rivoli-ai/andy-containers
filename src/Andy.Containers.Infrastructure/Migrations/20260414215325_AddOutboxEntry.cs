using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageBuildRecords_ImageReference",
                table: "ImageBuildRecords");

            migrationBuilder.DropIndex(
                name: "IX_ImageBuildRecords_TemplateCode",
                table: "ImageBuildRecords");

            migrationBuilder.AddColumn<string>(
                name: "Architecture",
                table: "ImageBuildRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ImageCreatedAt",
                table: "ImageBuildRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageDigest",
                table: "ImageBuildRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ImageSizeBytes",
                table: "ImageBuildRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LayerCount",
                table: "ImageBuildRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Os",
                table: "ImageBuildRecords",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OutboxEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PayloadType = table.Column<string>(type: "text", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageBuildRecords_ImageReference",
                table: "ImageBuildRecords",
                column: "ImageReference");

            migrationBuilder.CreateIndex(
                name: "IX_ImageBuildRecords_TemplateCode",
                table: "ImageBuildRecords",
                column: "TemplateCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEntries_PublishedAt_CreatedAt",
                table: "OutboxEntries",
                columns: new[] { "PublishedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEntries_Subject",
                table: "OutboxEntries",
                column: "Subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxEntries");

            migrationBuilder.DropIndex(
                name: "IX_ImageBuildRecords_ImageReference",
                table: "ImageBuildRecords");

            migrationBuilder.DropIndex(
                name: "IX_ImageBuildRecords_TemplateCode",
                table: "ImageBuildRecords");

            migrationBuilder.DropColumn(
                name: "Architecture",
                table: "ImageBuildRecords");

            migrationBuilder.DropColumn(
                name: "ImageCreatedAt",
                table: "ImageBuildRecords");

            migrationBuilder.DropColumn(
                name: "ImageDigest",
                table: "ImageBuildRecords");

            migrationBuilder.DropColumn(
                name: "ImageSizeBytes",
                table: "ImageBuildRecords");

            migrationBuilder.DropColumn(
                name: "LayerCount",
                table: "ImageBuildRecords");

            migrationBuilder.DropColumn(
                name: "Os",
                table: "ImageBuildRecords");

            migrationBuilder.CreateIndex(
                name: "IX_ImageBuildRecords_ImageReference",
                table: "ImageBuildRecords",
                column: "ImageReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageBuildRecords_TemplateCode",
                table: "ImageBuildRecords",
                column: "TemplateCode");
        }
    }
}
