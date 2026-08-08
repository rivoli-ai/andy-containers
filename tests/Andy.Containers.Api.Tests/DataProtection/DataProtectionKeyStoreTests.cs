using Andy.Containers.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Containers.Api.Tests.DataProtection;

/// <summary>
/// RC2 (#200). Pins the multi-replica contract: Data Protection keys
/// persisted in <see cref="ContainersDbContext"/> must be readable from
/// every replica that shares the database, so cookies / anti-forgery
/// tokens issued by one pod decrypt cleanly on every other.
/// </summary>
/// <remarks>
/// <para>
/// We exercise the contract against SQLite-shared-:memory:: a single
/// <see cref="SqliteConnection"/> backs two independent DI containers
/// (≈ two API replicas) so the test reproduces the cross-pod scenario
/// without needing Testcontainers / a real Postgres for the unit
/// suite. The Postgres path is the same code (EF Core + the same
/// <c>DataProtectionKeys</c> table); the integration smoke against a
/// real cluster lands in RC4's chart smoke after the chart exists.
/// </para>
/// <para>
/// <b>Why <c>SetApplicationName</c> matters here:</b> Data Protection
/// derives a per-app key. Two providers that share a key store but
/// disagree on application name will not decrypt each other's
/// payloads. Production pins it via <c>SetApplicationName("andy-containers")</c>
/// in <c>Program.cs</c>; tests must match.
/// </para>
/// </remarks>
public sealed class DataProtectionKeyStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DataProtectionKeyStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Materialise the schema once on the shared connection so both
        // "replicas" see the DataProtectionKeys table.
        using var seedCtx = NewContext();
        seedCtx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ContainersDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseSqlite(_connection).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ContainersDbContext(options);
    }

    /// <summary>
    /// Build a Data Protection provider whose key ring is persisted to
    /// a fresh <see cref="ContainersDbContext"/> over the shared SQLite
    /// connection. Each call models a separate replica's DI container.
    /// </summary>
    private ServiceProvider BuildReplica()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ContainersDbContext>(opts => opts.UseSqlite(_connection).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddDataProtection()
            .SetApplicationName("andy-containers")
            .PersistKeysToDbContext<ContainersDbContext>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ContainersDbContext_ImplementsIDataProtectionKeyContext()
    {
        // Defensive: if a future refactor drops the interface, the
        // PersistKeysToDbContext<> registration call would still type-
        // check (the constraint is satisfied by the cast at call time)
        // — but the framework would fail at runtime when it tries to
        // resolve IDataProtectionKeyContext from DI. Pin the interface
        // here so the failure surfaces at compile time.
        typeof(IDataProtectionKeyContext).IsAssignableFrom(typeof(ContainersDbContext))
            .Should().BeTrue();
        using var ctx = NewContext();
        ctx.DataProtectionKeys.Should().NotBeNull();
    }

    [Fact]
    public void Protect_OnReplicaA_Unprotect_OnReplicaB_RoundTripsPayload()
    {
        // The headline invariant: two API pods sharing one database
        // must decrypt each other's payloads. Without DB-backed keys
        // (RC2's predecessor: per-pod filesystem volume) this fails —
        // each pod generates its own ephemeral key.
        const string purpose = "andy.tests.dp.cross-replica";
        const string secret = "csrf-token-payload-123";

        using var replicaA = BuildReplica();
        using var replicaB = BuildReplica();

        var protectorA = replicaA.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(purpose);
        var protectorB = replicaB.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(purpose);

        var ciphertext = protectorA.Protect(secret);
        var roundTripped = protectorB.Unprotect(ciphertext);

        roundTripped.Should().Be(
            secret,
            "any cookie / anti-forgery token issued by one replica must " +
            "decrypt cleanly on every other replica that shares the DB");
    }

    [Fact]
    public void KeyRing_PersistsAcrossReplicaRestart()
    {
        // Models the rolling-restart scenario: replica A protects, then
        // gets recycled (DI rebuild). A new replica A' inherits the key
        // ring from the DB and decrypts the original payload. Without
        // DB persistence the new pod would generate a fresh key and the
        // old payload would be unreadable.
        const string purpose = "andy.tests.dp.restart";
        const string secret = "long-lived-cookie";

        byte[] ciphertext;
        using (var beforeRestart = BuildReplica())
        {
            ciphertext = beforeRestart
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(purpose)
                .Protect(System.Text.Encoding.UTF8.GetBytes(secret));
        }

        using var afterRestart = BuildReplica();
        var roundTripped = afterRestart
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(purpose)
            .Unprotect(ciphertext);

        System.Text.Encoding.UTF8.GetString(roundTripped).Should().Be(secret);
    }

    [Fact]
    public void KeyRing_IsWrittenToDataProtectionKeysTable()
    {
        // Defends against silent fallback to the in-memory key ring —
        // if PersistKeysToDbContext<> is mis-wired or the DbContext
        // doesn't expose IDataProtectionKeyContext, the framework
        // will quietly fall back without breaking the round-trip
        // tests above. Asserting an actual row is the canary.
        const string purpose = "andy.tests.dp.persistence-canary";

        using (var replica = BuildReplica())
        {
            replica.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(purpose)
                .Protect("forces-key-generation");
        }

        using var ctx = NewContext();
        ctx.DataProtectionKeys.Count().Should().BeGreaterThan(
            0,
            "the act of protecting a payload must materialise at least " +
            "one key row in the shared store; otherwise the framework " +
            "is silently using an ephemeral in-memory key ring");
    }
}
