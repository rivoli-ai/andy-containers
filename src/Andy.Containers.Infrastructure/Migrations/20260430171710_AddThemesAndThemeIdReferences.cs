using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddThemesAndThemeIdReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThemeId",
                table: "Templates",
                type: "character varying(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThemeId",
                table: "Containers",
                type: "character varying(64)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Themes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PaletteJson = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Themes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Templates_ThemeId",
                table: "Templates",
                column: "ThemeId");

            migrationBuilder.CreateIndex(
                name: "IX_Containers_ThemeId",
                table: "Containers",
                column: "ThemeId");

            migrationBuilder.CreateIndex(
                name: "IX_Themes_Kind",
                table: "Themes",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_Themes_Name",
                table: "Themes",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Containers_Themes_ThemeId",
                table: "Containers",
                column: "ThemeId",
                principalTable: "Themes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Templates_Themes_ThemeId",
                table: "Templates",
                column: "ThemeId",
                principalTable: "Themes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Containers_Themes_ThemeId",
                table: "Containers");

            migrationBuilder.DropForeignKey(
                name: "FK_Templates_Themes_ThemeId",
                table: "Templates");

            migrationBuilder.DropTable(
                name: "Themes");

            migrationBuilder.DropIndex(
                name: "IX_Templates_ThemeId",
                table: "Templates");

            migrationBuilder.DropIndex(
                name: "IX_Containers_ThemeId",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ThemeId",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "ThemeId",
                table: "Containers");
        }
    }
}
