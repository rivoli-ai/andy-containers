using Andy.Containers.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andy.Containers.Infrastructure.Migrations;

[DbContext(typeof(ContainersDbContext))]
[Migration("20260723180000_AddRunAttemptCorrelation")]
public partial class AddRunAttemptCorrelation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "AttemptId",
            table: "Runs",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.CreateIndex(
            name: "IX_Runs_CorrelationId_AttemptId",
            table: "Runs",
            columns: new[] { "CorrelationId", "AttemptId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Runs_CorrelationId_AttemptId",
            table: "Runs");

        migrationBuilder.DropColumn(
            name: "AttemptId",
            table: "Runs");
    }
}
