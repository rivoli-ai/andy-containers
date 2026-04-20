using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgentRevision = table.Column<int>(type: "integer", nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EnvironmentProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceRef_WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceRef_Branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContainerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_AgentId",
                table: "Runs",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_CorrelationId",
                table: "Runs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Status",
                table: "Runs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Runs");
        }
    }
}
