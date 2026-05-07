using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImageManagementSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BuildArtifactId",
                table: "Images",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BuildArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Digest = table.Column<string>(type: "text", nullable: false),
                    MediaType = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SpecHash = table.Column<string>(type: "text", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildBackendId = table.Column<string>(type: "text", nullable: false),
                    BuiltBy = table.Column<string>(type: "text", nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BuildLog = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildArtifacts_Templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "Templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImageSignatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    PayloadDigest = table.Column<string>(type: "text", nullable: false),
                    CertificateChain = table.Column<string>(type: "text", nullable: true),
                    TransparencyLogEntry = table.Column<string>(type: "text", nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageSignatures_BuildArtifacts_BuildArtifactId",
                        column: x => x.BuildArtifactId,
                        principalTable: "BuildArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegistryReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistryId = table.Column<string>(type: "text", nullable: false),
                    RepoPath = table.Column<string>(type: "text", nullable: false),
                    Tag = table.Column<string>(type: "text", nullable: false),
                    PushedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PushedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistryReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistryReferences_BuildArtifacts_BuildArtifactId",
                        column: x => x.BuildArtifactId,
                        principalTable: "BuildArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Images_BuildArtifactId",
                table: "Images",
                column: "BuildArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildArtifacts_Digest",
                table: "BuildArtifacts",
                column: "Digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildArtifacts_SpecHash",
                table: "BuildArtifacts",
                column: "SpecHash");

            migrationBuilder.CreateIndex(
                name: "IX_BuildArtifacts_TemplateId_SpecHash",
                table: "BuildArtifacts",
                columns: new[] { "TemplateId", "SpecHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageSignatures_BuildArtifactId",
                table: "ImageSignatures",
                column: "BuildArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistryReferences_BuildArtifactId",
                table: "RegistryReferences",
                column: "BuildArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistryReferences_RegistryId_RepoPath_Tag",
                table: "RegistryReferences",
                columns: new[] { "RegistryId", "RepoPath", "Tag" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Images_BuildArtifacts_BuildArtifactId",
                table: "Images",
                column: "BuildArtifactId",
                principalTable: "BuildArtifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Images_BuildArtifacts_BuildArtifactId",
                table: "Images");

            migrationBuilder.DropTable(
                name: "ImageSignatures");

            migrationBuilder.DropTable(
                name: "RegistryReferences");

            migrationBuilder.DropTable(
                name: "BuildArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_Images_BuildArtifactId",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "BuildArtifactId",
                table: "Images");
        }
    }
}
