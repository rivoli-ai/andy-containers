// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Containers.Abstractions;
using Andy.Containers.Api.Data;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using Andy.Containers.Validation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Data;

/// <summary>
/// rivoli-ai/andy-tasks#390. Guards for the pre-baked agent image
/// (an immutable revision-tagged <c>andy-agent-cli</c>) that replaces the in-container
/// clone + <c>dotnet publish</c> of andy-cli (&gt;5 minutes per container,
/// the cause of every cold plan execution blowing andy-tasks'
/// <c>ProvisionReadyTimeout</c>).
///
/// Layers covered here:
/// L0 build artifact — the Dockerfile exists, is bundled into the API build
/// output (deployed daemons have no repo checkout), and bakes the invariants
/// the fast path depends on (published andy-cli on PATH, /etc/andy/prebaked
/// marker, a --version smoke that fails a broken image build).
/// Pure decision seams — image→build-context mapping, locally-built
/// classification, and the provider-aware fallback that keeps non-Docker
/// providers on the legacy ubuntu + source-build path.
/// Seeding — the andy-cli-dev template moves to the pre-baked image (fresh
/// seed AND upgrade of an existing DB) and its post_create script carries
/// both the fast path and the source-build fallback.
/// </summary>
public class AgentCliPrebakedImageTests
{
    // ---------------------------------------------------------------
    // L0 — build artifact
    // ---------------------------------------------------------------

    [Fact]
    public void Dockerfile_IsBundledIntoBuildOutput()
    {
        // The csproj Content rule must land the Dockerfile under
        // images/agent-cli/ next to the binaries — that is what lets a
        // DEPLOYED daemon (publish output, no repo checkout) build the
        // image. This test project references the API project, so the
        // same Content rule flows into this test's output directory.
        var bundled = Path.Combine(AppContext.BaseDirectory, "images", "agent-cli", "Dockerfile");
        File.Exists(bundled).Should().BeTrue(
            $"Andy.Containers.Api.csproj must bundle images/agent-cli/Dockerfile (expected at {bundled}); " +
            "without it a deployed daemon cannot build the revision-tagged agent image and every " +
            "container falls back to the >5-minute in-container source build.");
    }

    [Fact]
    public void FindImageBuildDirectory_LocatesAgentCliContext()
    {
        var dir = DockerInfrastructureProvider.FindImageBuildDirectory("agent-cli");
        dir.Should().NotBeNull(
            "the agent-cli build context must be locatable from either the repo checkout (CWD walk) " +
            "or the build output (AppContext.BaseDirectory)");
        File.Exists(Path.Combine(dir!, "Dockerfile")).Should().BeTrue();
    }

    [Fact]
    public void Dockerfile_BakesTheFastPathInvariants()
    {
        var dir = DockerInfrastructureProvider.FindImageBuildDirectory("agent-cli");
        dir.Should().NotBeNull();
        var dockerfile = File.ReadAllText(Path.Combine(dir!, "Dockerfile"));

        // Same base the template used before #390 — keeps runtime behaviour
        // (apt, paths, openssh) identical for everything else in the chain.
        dockerfile.Should().Contain("FROM ubuntu:24.04");

        // The whole point: andy-cli is published at IMAGE build time…
        dockerfile.Should().Contain("dotnet publish /opt/andy-cli-src/src/Andy.Cli/Andy.Cli.csproj",
            "the image must pay the publish cost once so containers don't");
        dockerfile.Should().Contain("ln -sf /opt/andy-cli/andy-cli /usr/local/bin/andy-cli",
            "HeadlessRunner execs `andy-cli` by name — it must be on PATH");

        // …and a broken binary can never be tagged.
        dockerfile.Should().Contain("andy-cli --version",
            "the image build must smoke-test the binary so a bad image fails the build, not the run");

        dockerfile.Should().Contain("ARG ANDY_CLI_GIT_REF");
        dockerfile.Should().NotContain("ARG ANDY_CLI_GIT_REF=main",
            "the application source must be supplied as an immutable release input");
        dockerfile.Should().Contain("fetch --depth 1 origin \"$ANDY_CLI_GIT_REF\"");
        dockerfile.Should().Contain("org.opencontainers.image.revision=$ANDY_CLI_GIT_REF");

        // The marker DataSeeder's post_create fast path keys on.
        dockerfile.Should().Contain("/etc/andy/prebaked",
            "the seeded post_create script takes its fast path iff this marker exists");

        // sshd + tmux etc. must be pre-installed for the runtime-only fast path.
        foreach (var pkg in new[] { "openssh-server", "tmux", "dtach", "git", "curl", "locales" })
        {
            dockerfile.Should().Contain(pkg, $"the fast path skips apt entirely, so {pkg} must be baked in");
        }
    }

    // ---------------------------------------------------------------
    // Pure decision seams
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("andy-agent-cli:3f08f5bb340e", "agent-cli")]
    [InlineData("andy-agent-cli:latest", "agent-cli")]
    [InlineData("andy-desktop-python:latest", "desktop-python")]
    [InlineData("andy-desktop-dotnet:latest", "desktop-dotnet")]
    [InlineData("andy-devpilot-desktop:latest", "devpilot-desktop")]
    public void ImageBuildContextName_MapsReferenceToImagesSubdirectory(string reference, string expected)
    {
        DockerInfrastructureProvider.ImageBuildContextName(reference).Should().Be(expected);
    }

    [Theory]
    [InlineData("andy-agent-cli:3f08f5bb340e", true)]
    [InlineData("andy-agent-cli:latest", true)]
    [InlineData("andy-agent-cli", true)]
    [InlineData("andy-desktop-python:latest", true)]
    [InlineData("andy-devpilot-desktop:latest", true)]
    [InlineData("ubuntu:24.04", false)]
    [InlineData("mcr.microsoft.com/dotnet/sdk:8.0-alpine", false)]
    [InlineData("andy-agent-cli-evil:latest", false)]
    public void LocalImages_ClassifiesLocallyBuiltReferences(string reference, bool expected)
    {
        LocalImages.IsLocallyBuilt(reference).Should().Be(expected);
    }

    [Fact]
    public void AgentCliImage_IsExemptFromDigestPin_LikeDesktopImages()
    {
        // The #125 strict mode must not reject the locally-built agent image:
        // it never transits a registry, so tag substitution can't reach it.
        LocalImages.IsLocallyBuilt(LocalImages.AgentCli).Should().BeTrue();
        OciReferenceValidator.IsDigestPinned(LocalImages.AgentCli).Should().BeFalse(
            "sanity: the reference is a mutable tag, so ONLY the LocalImages exemption admits it");
    }

    [Theory]
    [InlineData(ProviderType.Docker, "andy-agent-cli:latest", LocalImages.AgentCli)]
    [InlineData(ProviderType.Docker, LocalImages.AgentCli, LocalImages.AgentCli)]
    [InlineData(ProviderType.AppleContainer, "andy-agent-cli:latest", "ubuntu:24.04")]
    [InlineData(ProviderType.AwsFargate, "andy-agent-cli:latest", "ubuntu:24.04")]
    [InlineData(ProviderType.AppleContainer, "ubuntu:24.04", "ubuntu:24.04")]
    // Desktop fixtures are intentionally NOT adjusted: non-Docker desktop
    // provisioning already failed pre-#390 and silently swapping the image
    // would hide that, whereas the agent image is the default plan-execution
    // template and must keep working (via source-build) on every provider.
    [InlineData(ProviderType.AppleContainer, "andy-desktop-python:latest", "andy-desktop-python:latest")]
    public void ResolveEffectiveImageForProvider_FallsBackOnlyForAgentImageOnNonDocker(
        ProviderType providerType, string requested, string expected)
    {
        ContainerOrchestrationService.ResolveEffectiveImageForProvider(requested, providerType)
            .Should().Be(expected);
    }

    [Fact]
    public void BuildLocalImageArguments_LoadsAndPinsAgentImage()
    {
        var arguments = DockerInfrastructureProvider.BuildLocalImageArguments(
            LocalImages.AgentCli,
            "/repo/images/agent-cli",
            "/repo/scripts/container",
            "unix:///custom/docker.sock");

        arguments.Should().ContainInOrder("--host", "unix:///custom/docker.sock", "buildx");
        arguments.Should().ContainInOrder("buildx", "build", "--load", "-t", LocalImages.AgentCli);
        arguments.Should().ContainInOrder(
            "--build-arg", $"ANDY_CLI_GIT_REF={LocalImages.AgentCliGitRevision}");
        arguments.Should().ContainInOrder(
            "--build-context", "scripts=/repo/scripts/container", "/repo/images/agent-cli");
    }

    [Theory]
    [InlineData("tcp://docker.example.test:2376")]
    [InlineData("unix:///var/run/non-default-docker.sock")]
    public void BuildLocalImageArguments_TargetsConfiguredDockerEndpoint(string endpoint)
    {
        var arguments = DockerInfrastructureProvider.BuildLocalImageArguments(
            "andy-desktop-python:latest",
            "/repo/images/desktop-python",
            scriptsDirectory: null,
            dockerEndpoint: endpoint);

        arguments.Take(3).Should().Equal("--host", endpoint, "buildx");
    }

    [Theory]
    [InlineData(LocalImages.AgentCliGitRevision, true)]
    [InlineData("0000000000000000000000000000000000000000", false)]
    [InlineData(null, false)]
    public void MatchesExpectedLocalImageIdentity_RejectsStaleOrUnlabelledAgentImage(
        string? actualRevision, bool expected)
    {
        DockerInfrastructureProvider.MatchesExpectedLocalImageIdentity(
                LocalImages.AgentCli, actualRevision)
            .Should().Be(expected);
    }

    [Fact]
    public void MatchesExpectedLocalImageIdentity_NonAgentImagesUseTheirTagIdentity()
    {
        DockerInfrastructureProvider.MatchesExpectedLocalImageIdentity(
                "andy-desktop-python:latest", actualRevision: null)
            .Should().BeTrue();
    }

    [Fact]
    public async Task LocalImageBuildCoordinator_ConcurrentCallersShareOneBuild()
    {
        var key = $"test-build-{Guid.NewGuid():N}";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        async Task Build(CancellationToken ct)
        {
            Interlocked.Increment(ref buildCount);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        }

        var first = LocalImageBuildCoordinator.RunAsync(key, Build, CancellationToken.None);
        await started.Task;
        var second = LocalImageBuildCoordinator.RunAsync(key, Build, CancellationToken.None);

        release.SetResult();
        await Task.WhenAll(first, second);

        buildCount.Should().Be(1, "warm-up and first-use provisioning must join one build");
    }

    [Fact]
    public async Task LocalImageBuildCoordinator_LastCancelledWaiterCancelsBuildAndAllowsRetry()
    {
        var key = $"test-cancel-{Guid.NewGuid():N}";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;
        using var waiterCancellation = new CancellationTokenSource();

        async Task Build(CancellationToken ct)
        {
            Interlocked.Increment(ref buildCount);
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                buildCancelled.TrySetResult();
                throw;
            }
        }

        var wait = LocalImageBuildCoordinator.RunAsync(key, Build, waiterCancellation.Token);
        await started.Task;
        waiterCancellation.Cancel();

        await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();
        await buildCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await LocalImageBuildCoordinator.RunAsync(
            key,
            _ =>
            {
                Interlocked.Increment(ref buildCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        buildCount.Should().Be(2, "a cancelled flight must be removed so a later caller can retry");
    }

    [Fact]
    public async Task LocalImageBuildCoordinator_SharesFailureAndAllowsLaterRetry()
    {
        var key = $"test-failure-{Guid.NewGuid():N}";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        async Task FailingBuild(CancellationToken ct)
        {
            Interlocked.Increment(ref buildCount);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            throw new InvalidOperationException("build failed");
        }

        var first = LocalImageBuildCoordinator.RunAsync(key, FailingBuild, CancellationToken.None);
        await started.Task;
        var second = LocalImageBuildCoordinator.RunAsync(key, FailingBuild, CancellationToken.None);
        release.SetResult();

        (await FluentActions.Awaiting(() => first).Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("build failed");
        (await FluentActions.Awaiting(() => second).Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("build failed");
        buildCount.Should().Be(1, "all waiters must observe the same in-flight failure");

        await LocalImageBuildCoordinator.RunAsync(
            key,
            _ =>
            {
                Interlocked.Increment(ref buildCount);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        buildCount.Should().Be(2, "a failed flight must not poison later retries");
    }

    // ---------------------------------------------------------------
    // Seeding — fresh seed, script shape, and existing-DB upgrade
    // ---------------------------------------------------------------

    [Fact]
    public async Task Seed_AndyCliDevTemplate_UsesPrebakedAgentImage()
    {
        using var db = InMemoryDbHelper.CreateContext();
        await DataSeeder.SeedAsync(db);

        var template = await db.Templates.FirstAsync(t => t.Code == "andy-cli-dev");
        template.BaseImage.Should().Be(LocalImages.AgentCli,
            "the template must provision from the pre-baked image — ubuntu:24.04 here " +
            "IS the >5-minute-provision bug (rivoli-ai/andy-tasks#390)");
    }

    [Fact]
    public async Task Seed_AndyCliScript_HasFastPathAndSourceBuildFallback()
    {
        using var db = InMemoryDbHelper.CreateContext();
        await DataSeeder.SeedAsync(db);

        var template = await db.Templates.FirstAsync(t => t.Code == "andy-cli-dev");
        // Scripts is JSON with the default STJ encoder (escapes &, >, ');
        // decode post_create so the assertions read like the shell script.
        var scripts = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(template.Scripts!)!;
        var script = scripts["post_create"];

        // Fast path: keyed on the marker the Dockerfile writes AND a runnable
        // binary, so a plain base image can never mistakenly skip the install.
        script.Should().Contain("if [ -f /etc/andy/prebaked ] && command -v andy-cli",
            "post_create must take the runtime-only fast path on the pre-baked image");

        // Fallback: the legacy source-build chain survives for containers that
        // land on a plain base image (non-Docker providers).
        script.Should().Contain("git clone --depth 1 https://github.com/rivoli-ai/andy-cli.git",
            "non-Docker providers fall back to ubuntu:24.04 and must still get andy-cli");
        script.Should().Contain("dotnet publish /opt/andy-cli-src/src/Andy.Cli/Andy.Cli.csproj");

        // The loud final check survives on BOTH paths — a silent miss is the
        // exit-127 bug all over again.
        script.Should().Contain("fi && command -v andy-cli >/dev/null 2>&1");
    }

    [Fact]
    public async Task Seed_ExistingDb_UpgradesAndyCliDevToPrebakedImage()
    {
        // Simulate a deployment seeded BEFORE #390: template already exists
        // with the legacy base image and script. Re-running SeedAsync (what
        // every service start does) must migrate it forward — otherwise
        // existing user DBs keep paying the 5-minute provision forever.
        using var db = InMemoryDbHelper.CreateContext();
        await DataSeeder.SeedAsync(db);

        var template = await db.Templates.FirstAsync(t => t.Code == "andy-cli-dev");
        template.BaseImage = "ubuntu:24.04";
        template.Scripts = /*lang=json*/ """{"post_create":"legacy"}""";
        await db.SaveChangesAsync();

        await DataSeeder.SeedAsync(db);

        var upgraded = await db.Templates.AsNoTracking().FirstAsync(t => t.Code == "andy-cli-dev");
        upgraded.BaseImage.Should().Be(LocalImages.AgentCli,
            "UpdateTemplateScriptsAsync must move existing DBs to the pre-baked image");
        upgraded.Scripts.Should().Contain("prebaked",
            "the script must be refreshed alongside the image");
    }

    // ---------------------------------------------------------------
    // Startup warmer — config gate + graceful skips
    // ---------------------------------------------------------------

    [Fact]
    public async Task Warmer_ConfigGate_SkipsWithoutTouchingProviders()
    {
        using var db = InMemoryDbHelper.CreateContext();
        var factory = new ThrowingProviderFactory(); // would throw if consulted
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AgentCliImageWarmer.WarmOnStartupKey] = "false"
            })
            .Build();

        var warmer = new AgentCliImageWarmer(
            InMemoryDbHelper.CreateScopeFactory(db), factory, config,
            NullLogger<AgentCliImageWarmer>.Instance);

        await warmer.StartAsync(CancellationToken.None);
        await (warmer.ExecuteTask ?? Task.CompletedTask);

        factory.Consulted.Should().BeFalse("a disabled warmer must not resolve any provider");
    }

    [Fact]
    public async Task Warmer_NoDockerProviderSeeded_SkipsGracefully()
    {
        using var db = InMemoryDbHelper.CreateContext(); // no providers seeded
        var factory = new ThrowingProviderFactory();
        var config = new ConfigurationBuilder().Build();

        var warmer = new AgentCliImageWarmer(
            InMemoryDbHelper.CreateScopeFactory(db), factory, config,
            NullLogger<AgentCliImageWarmer>.Instance);

        await warmer.StartAsync(CancellationToken.None);
        var run = async () => await (warmer.ExecuteTask ?? Task.CompletedTask);

        await run.Should().NotThrowAsync("warm-up is strictly best-effort");
        factory.Consulted.Should().BeFalse();
    }

    private sealed class ThrowingProviderFactory : IInfrastructureProviderFactory
    {
        public bool Consulted { get; private set; }

        public IInfrastructureProvider GetProvider(InfrastructureProvider providerEntity)
        {
            Consulted = true;
            throw new InvalidOperationException("provider must not be resolved in this scenario");
        }

        public IInfrastructureProvider GetProvider(ProviderType type)
        {
            Consulted = true;
            throw new InvalidOperationException("provider must not be resolved in this scenario");
        }
    }
}
