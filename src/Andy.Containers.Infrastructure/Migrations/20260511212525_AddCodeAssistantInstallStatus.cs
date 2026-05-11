using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeAssistantInstallStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CodeAssistantStatus",
                table: "Containers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CodeAssistantStatusAt",
                table: "Containers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodeAssistantStatusReason",
                table: "Containers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeAssistantStatus",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "CodeAssistantStatusAt",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "CodeAssistantStatusReason",
                table: "Containers");
        }
    }
}
