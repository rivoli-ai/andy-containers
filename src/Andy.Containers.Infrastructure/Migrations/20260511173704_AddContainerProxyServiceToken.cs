using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerProxyServiceToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProxyServiceToken",
                table: "Containers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProxyServiceTokenId",
                table: "Containers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProxyTokenIssuedAt",
                table: "Containers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyServiceToken",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ProxyServiceTokenId",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ProxyTokenIssuedAt",
                table: "Containers");
        }
    }
}
