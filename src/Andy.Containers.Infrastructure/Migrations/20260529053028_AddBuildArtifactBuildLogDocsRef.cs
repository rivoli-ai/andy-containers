using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildArtifactBuildLogDocsRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BuildLogDocsRefDocumentId",
                table: "BuildArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BuildLogDocsRefLinkId",
                table: "BuildArtifacts",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuildLogDocsRefDocumentId",
                table: "BuildArtifacts");

            migrationBuilder.DropColumn(
                name: "BuildLogDocsRefLinkId",
                table: "BuildArtifacts");
        }
    }
}
