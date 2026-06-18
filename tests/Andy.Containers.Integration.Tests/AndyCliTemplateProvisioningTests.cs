// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// Real-Docker integration proof for the `andy-cli-dev` template's hard
/// dependency: the HeadlessRunner (AP6) execs `andy-cli run --headless …`
/// INSIDE a container provisioned from this template, so andy-cli MUST be a
/// runnable command on PATH after the template's post-create script runs.
///
/// The bug this guards against: the original `andy-cli-dev` seed used a
/// generic base-packages-only post-create script that installed nothing
/// andy-cli-related, so every headless run died with exit 127
/// ("andy-cli: not found") in ~0.1s. ADR-0003 names andy-cli a hard
/// dependency the agent container is expected to ship.
///
/// This test creates a REAL Docker container from the template's REAL base
/// image, runs the REAL seeded post-create script (read out of DataSeeder so
/// the test can never drift from production), then asserts via real
/// `docker exec` that `andy-cli` resolves on PATH (exit 0) and that the exact
/// command shape HeadlessRunner builds does NOT exit 127. No mocks — the full
/// provision → exec chain is real.
///
/// NOTE: the install builds andy-cli from public source (.NET 8 SDK +
/// `dotnet publish`), which takes several minutes. The fact is gated behind
/// DockerCliFactAttribute and given a generous timeout.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Docker")]
public sealed class AndyCliTemplateProvisioningTests : IAsyncLifetime
{
    private readonly DockerInfrastructureProvider _provider;
    private readonly ITestOutputHelper _output;
    private string? _externalId;

    public AndyCliTemplateProvisioningTests(ITestOutputHelper output)
    {
        _output = output;
        _provider = new DockerInfrastructureProvider(
            null, NullLoggerFactory.Instance.CreateLogger<DockerInfrastructureProvider>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_externalId is not null)
        {
            try { await _provider.DestroyContainerAsync(_externalId, CancellationToken.None); }
            catch { /* best-effort cleanup */ }
        }
    }

    [DockerCliFact(Timeout = 900_000)]
    public async Task AndyCliDevTemplate_ProvisionsRunnableAndyCliOnPath()
    {
        // 1. Pull the REAL seeded template (image + post-create script) out of
        //    DataSeeder via a throwaway in-memory DB — so the test exercises
        //    exactly what production seeds, not a hand-copied script.
        var (baseImage, postCreate) = await ReadAndyCliDevTemplateAsync();
        baseImage.Should().NotBeNullOrWhiteSpace();
        postCreate.Should().NotBeNullOrWhiteSpace(
            "the andy-cli-dev template must define a post_create script");
        postCreate.Should().Contain("andy-cli",
            "the andy-cli-dev post-create script must actually provision andy-cli — " +
            "a base-packages-only script is the exit-127 bug.");

        // 2. Create a REAL container from the template's REAL base image.
        var spec = new ContainerSpec
        {
            Name = $"andycli-tmpl-it-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = baseImage,
            Resources = new ResourceSpec { CpuCores = 2, MemoryMb = 4096 },
        };
        var created = await _provider.CreateContainerAsync(spec, CancellationToken.None);
        _externalId = created.ExternalId;
        created.Status.Should().Be(ContainerStatus.Running);

        // 3. Run the REAL seeded post-create script (the provisioning step).
        _output.WriteLine("Running andy-cli-dev post-create script (this builds andy-cli from source — minutes)…");
        var setup = await _provider.ExecAsync(
            _externalId!, postCreate, TimeSpan.FromMinutes(13), CancellationToken.None);
        _output.WriteLine($"post-create exit={setup.ExitCode}");
        if (setup.ExitCode != 0)
        {
            _output.WriteLine($"---post-create stdout---\n{setup.StdOut}");
            _output.WriteLine($"---post-create stderr---\n{setup.StdErr}");
        }
        setup.ExitCode.Should().Be(0,
            "the post-create script ends with `command -v andy-cli`, so a non-zero exit " +
            $"means andy-cli was not provisioned.\nstderr:\n{setup.StdErr}");

        // 4. Independently assert andy-cli resolves on PATH via real docker exec.
        var which = await _provider.ExecAsync(
            _externalId!, "command -v andy-cli", CancellationToken.None);
        which.ExitCode.Should().Be(0,
            $"andy-cli must be a runnable command on PATH; got stderr:\n{which.StdErr}");
        which.StdOut.Should().Contain("andy-cli");

        // 5. Run the EXACT command shape HeadlessRunner builds and prove it is
        //    NOT exit 127 ("not found"). We feed an empty config, so a runnable
        //    binary will reject it (config-validation exit 2) — anything except
        //    127 proves the binary executed. 127 is the regression.
        var headless = await _provider.ExecAsync(
            _externalId!,
            "echo '{}' > /tmp/andy-cli-it.json && andy-cli run --headless --config /tmp/andy-cli-it.json",
            TimeSpan.FromMinutes(2), CancellationToken.None);
        _output.WriteLine($"headless exit={headless.ExitCode}");
        _output.WriteLine($"---headless stdout---\n{headless.StdOut}");
        _output.WriteLine($"---headless stderr---\n{headless.StdErr}");
        headless.ExitCode.Should().NotBe(127,
            "exit 127 means `andy-cli` was not found on PATH — the exact bug. " +
            "A runnable binary executes and rejects the empty config instead.");
    }

    // Seed a throwaway sqlite DB and read the andy-cli-dev template so the test
    // uses the production base image + post-create script verbatim.
    private static async Task<(string BaseImage, string PostCreate)> ReadAndyCliDevTemplateAsync()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        await using var db = new ContainersDbContext(
            new DbContextOptionsBuilder<ContainersDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        await DataSeeder.SeedAsync(db);

        var template = await db.Templates.AsNoTracking()
            .SingleAsync(t => t.Code == "andy-cli-dev");

        var scripts = JsonSerializer.Deserialize<Dictionary<string, string>>(template.Scripts ?? "{}")
            ?? new Dictionary<string, string>();
        scripts.TryGetValue("post_create", out var postCreate);

        return (template.BaseImage, postCreate ?? string.Empty);
    }
}
