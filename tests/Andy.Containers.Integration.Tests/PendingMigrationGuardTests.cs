using Andy.Containers.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// Regression guard for Conductor #886's failure mode: adding a
/// new property to an EF entity (or a new <see cref="DbSet{T}"/>
/// to <see cref="ContainersDbContext"/>) WITHOUT generating an
/// accompanying migration via
/// <c>dotnet ef migrations add &lt;Name&gt;</c>.
///
/// What used to happen
/// -------------------
/// 1. Author edits a model: adds <c>public string? ThemeId</c>.
/// 2. Build passes (the model is happy).
/// 3. Tests pass (in-memory + fresh-SQLite paths use
///    <c>EnsureCreated</c>-style schema generation, which reads
///    the live model — never the migration set).
/// 4. PR ships.
/// 5. Existing users start the new build. <c>MigrateAsync</c>
///    finds nothing to apply because the migration set is
///    silent about the new property. Queries against the new
///    column fail at runtime: "no such column: t.ThemeId".
/// 6. Service crashes. User sees "container service failed to
///    start" with no useful reason. Sami #886-bug-thread.
///
/// What this test does
/// -------------------
/// Diffs the live <see cref="IModel"/> against the most recent
/// migration's snapshot. If they differ, ANY change has been
/// made to the model that isn't captured in a migration — and
/// the test fails with a hint to run
/// <c>dotnet ef migrations add &lt;Name&gt;</c>.
///
/// This is functionally equivalent to EF Core 9's
/// <c>HasPendingModelChanges()</c> — backported here because
/// we're on EF Core 8.
/// </summary>
[Trait("Category", "Integration")]
public class PendingMigrationGuardTests
{
    [Fact]
    public void Model_MatchesLatestMigrationSnapshot_NoPendingChanges()
    {
        // Build a context whose provider matches the one the
        // snapshot was generated against. Migrations in this
        // repo are scaffolded against Npgsql (the production
        // provider), so the snapshot uses Postgres-flavoured
        // column types like `uuid`, `jsonb`, `timestamp with time zone`.
        // If we ran the diff against the SQLite provider here,
        // EVERY column would show as an `AlterColumn` because
        // SQLite renders those types as `TEXT` — false-positive
        // drift caused by provider mismatch, not by the
        // genuine "did the author forget a migration" check we
        // want.
        //
        // We don't need a live Postgres connection — the
        // model-differ only consumes metadata. The connection
        // string is bogus and never opened.
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=__model_guard_only__",
                npgsql => npgsql.MigrationsAssembly("Andy.Containers.Infrastructure"))
            .Options;
        using var db = new ContainersDbContext(options);

        // Resolve the diffing services EF uses internally.
        var differ = db.GetService<IMigrationsModelDiffer>();
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();

        // The snapshot model is what the migration set thinks
        // the schema should look like; the design-time model is
        // what the live entity classes actually describe.
        //
        // EF returns the snapshot model in non-finalized form
        // (it was built by the snapshot's `BuildModel(...)`
        // method), so we have to run it through the runtime
        // initializer before `GetRelationalModel()` is callable.
        // The design-time model, by contrast, is already
        // finalized because it lives on the live DbContext.
        var rawSnapshot = migrationsAssembly.ModelSnapshot?.Model
            ?? throw new InvalidOperationException(
                "no ModelSnapshot found in the migrations assembly — there must be at least one migration");
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var snapshotModel = initializer.Initialize((IModel)rawSnapshot);
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;

        // Run them through the runtime relational model so the
        // diff sees actual SQL-level constructs (tables, columns,
        // FKs, indexes), not just CLR-level entity shape.
        var diffs = differ.GetDifferences(
            snapshotModel.GetRelationalModel(),
            designTimeModel.GetRelationalModel())
            .ToList();

        diffs.Should().BeEmpty(
            BuildHelpfulMessage(diffs));
    }

    /// <summary>
    /// Renders the operation list as an actionable error so the
    /// CI failure log tells you exactly which entities drifted
    /// and how to fix it. Without this, the assertion failure
    /// is just "Expected collection to be empty" — which doesn't
    /// help a human reading red CI logs at 11 PM.
    /// </summary>
    private static string BuildHelpfulMessage(IReadOnlyList<MigrationOperation> diffs)
    {
        if (diffs.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            $"Detected {diffs.Count} change(s) in the EF model that aren't captured in a migration. Conductor #886.",
            "",
            "Likely cause: someone added a property / DbSet / index / FK to ContainersDbContext or a model",
            "and forgot to generate the matching migration. The model is internally consistent (so unit tests",
            "pass against in-memory SQLite) but `MigrateAsync` does nothing for existing user DBs because",
            "no migration tells it to add the new column / table.",
            "",
            "Fix: from the andy-containers repo root, run:",
            "    dotnet ef migrations add <DescriptiveName> \\",
            "        --project src/Andy.Containers.Infrastructure \\",
            "        --startup-project src/Andy.Containers.Api",
            "",
            "Then commit the new files in src/Andy.Containers.Infrastructure/Migrations/.",
            "",
            "Operations the differ produced:",
        };

        foreach (var op in diffs)
        {
            lines.Add($"  - {op.GetType().Name}: {DescribeOperation(op)}");
        }

        return string.Join('\n', lines);
    }

    private static string DescribeOperation(MigrationOperation op)
        => op switch
        {
            CreateTableOperation t => $"CREATE TABLE {t.Name}",
            DropTableOperation t => $"DROP TABLE {t.Name}",
            AddColumnOperation c => $"ADD COLUMN {c.Table}.{c.Name} ({c.ClrType.Name})",
            DropColumnOperation c => $"DROP COLUMN {c.Table}.{c.Name}",
            AlterColumnOperation c => $"ALTER COLUMN {c.Table}.{c.Name}",
            CreateIndexOperation i => $"CREATE INDEX {i.Name} ON {i.Table}({string.Join(",", i.Columns)})",
            DropIndexOperation i => $"DROP INDEX {i.Name} ON {i.Table}",
            AddForeignKeyOperation fk => $"ADD FK {fk.Name} ON {fk.Table}",
            DropForeignKeyOperation fk => $"DROP FK {fk.Name} ON {fk.Table}",
            _ => op.ToString() ?? "(unknown)"
        };
}
