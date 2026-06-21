using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// Integration tests for DockerInfrastructureProvider.
/// Requires: Docker daemon running (Docker Desktop or colima).
/// These tests create/start/stop/destroy real Docker containers.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Docker")]
public class DockerProviderTests : IAsyncLifetime
{
    private readonly DockerInfrastructureProvider _provider;
    private string? _externalId;

    public DockerProviderTests()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<DockerInfrastructureProvider>();
        _provider = new DockerInfrastructureProvider(null, logger);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_externalId is not null)
        {
            try { await _provider.DestroyContainerAsync(_externalId, CancellationToken.None); }
            catch { /* ignore cleanup failures */ }
        }
    }

    [Fact]
    public async Task HealthCheck_WhenDockerRunning_ShouldReturnHealthy()
    {
        var health = await _provider.HealthCheckAsync(CancellationToken.None);

        health.Should().Be(ProviderHealth.Healthy);
    }

    [Fact]
    public async Task FullLifecycle_CreateStartExecStopDestroy()
    {
        // 1. Create container
        var spec = new ContainerSpec
        {
            Name = $"integration-test-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = "ubuntu:24.04",
            Resources = new ResourceSpec { CpuCores = 1, MemoryMb = 256 }
        };

        var result = await _provider.CreateContainerAsync(spec, CancellationToken.None);

        result.Should().NotBeNull();
        result.ExternalId.Should().NotBeNullOrEmpty();
        result.Status.Should().Be(ContainerStatus.Running);
        _externalId = result.ExternalId;

        // 2. Verify connection info
        var connInfo = await _provider.GetConnectionInfoAsync(_externalId, CancellationToken.None);
        connInfo.Should().NotBeNull();

        // 3. Verify container info shows running
        var info = await _provider.GetContainerInfoAsync(_externalId, CancellationToken.None);
        info.Status.Should().Be(ContainerStatus.Running);

        // 4. Execute a command inside the container
        var execResult = await _provider.ExecAsync(_externalId, "echo hello-from-docker", CancellationToken.None);
        execResult.ExitCode.Should().Be(0);
        execResult.StdOut.Should().Contain("hello-from-docker");

        // 5. Execute a command that fails
        var failResult = await _provider.ExecAsync(_externalId, "ls /nonexistent", CancellationToken.None);
        failResult.ExitCode.Should().NotBe(0);

        // 6. Stop the container
        await _provider.StopContainerAsync(_externalId, CancellationToken.None);

        var stoppedInfo = await _provider.GetContainerInfoAsync(_externalId, CancellationToken.None);
        stoppedInfo.Status.Should().Be(ContainerStatus.Stopped);

        // 7. Destroy the container
        await _provider.DestroyContainerAsync(_externalId, CancellationToken.None);
        _externalId = null;
    }

    [Fact]
    public async Task GetCapabilities_ShouldReturnDockerCapabilities()
    {
        var caps = await _provider.GetCapabilitiesAsync(CancellationToken.None);

        caps.Type.Should().Be(ProviderType.Docker);
        caps.SupportedArchitectures.Should().Contain("arm64");
        caps.SupportsExec.Should().BeTrue();
        caps.SupportsPortForwarding.Should().BeTrue();
    }

    [Fact]
    public async Task DestroyContainer_PhantomRow_TreatsNotFoundAsSuccess()
    {
        // Repro the user's "I can't destroy stale containers" bug:
        // andy-containers' DB still has rows pointing at containers
        // that the docker daemon has already removed out-of-band
        // (manual `docker rm`, host reboot, prune, …). Calling
        // DestroyContainerAsync against such a phantom must succeed
        // — the goal "make this container be gone" is already met,
        // and throwing here keeps the orchestration layer from
        // marking the DB row Destroyed, leaving it stuck forever.
        // Conductor #826 item 3.

        // Create a real container so we have a real externalId.
        var spec = new ContainerSpec
        {
            Name = $"phantom-test-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = "alpine:latest",
            Resources = new ResourceSpec { CpuCores = 1, MemoryMb = 64 }
        };
        var created = await _provider.CreateContainerAsync(spec, CancellationToken.None);
        var externalId = created.ExternalId;

        // Remove the container directly via Docker.DotNet, bypassing
        // andy-containers — simulating the out-of-band removal that
        // creates phantoms.
        var rawClient = new Docker.DotNet.DockerClientConfiguration(
            new Uri(File.Exists("/var/run/docker.sock")
                ? "unix:///var/run/docker.sock"
                : $"unix://{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock")}"))
            .CreateClient();
        await rawClient.Containers.RemoveContainerAsync(
            externalId,
            new Docker.DotNet.Models.ContainerRemoveParameters { Force = true });

        // Now destroy via our provider. Should succeed silently —
        // catches DockerContainerNotFoundException and treats it as
        // already-destroyed.
        var act = async () => await _provider.DestroyContainerAsync(externalId, CancellationToken.None);
        await act.Should().NotThrowAsync(
            "phantom containers must be destroyable so the user can clear stale DB rows");

        // Don't try to re-clean up in DisposeAsync — the container
        // is already gone.
        _externalId = null;
    }

    // exec working-dir feature. The exec endpoint historically ran
    // `sh -c "<command>"` with NO working directory, so every command ran in
    // the image's default WORKDIR rather than the repo checkout. This proves
    // the first-class WorkingDir field routes through Docker's native `-w`:
    //   * WorkingDir = "/tmp" ⇒ `pwd` reports /tmp (command ran THERE).
    //   * WorkingDir = null   ⇒ `pwd` reports the image WORKDIR (here `/`),
    //                            byte-identical to the historical behaviour.
    [Fact]
    public async Task Exec_WithWorkingDir_RunsCommandInThatDirectory_NullLeavesImageWorkdir()
    {
        var spec = new ContainerSpec
        {
            Name = $"workingdir-test-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = "alpine:latest",
            Resources = new ResourceSpec { CpuCores = 1, MemoryMb = 64 }
        };
        var created = await _provider.CreateContainerAsync(spec, CancellationToken.None);
        _externalId = created.ExternalId;

        // 1. A non-empty WorkingDir runs the command in that directory.
        var inTmp = await _provider.ExecAsync(
            _externalId!, "pwd", TimeSpan.FromSeconds(30), workingDir: "/tmp", CancellationToken.None);
        inTmp.ExitCode.Should().Be(0);
        inTmp.StdOut!.Trim().Should().Be("/tmp",
            "a non-empty WorkingDir must run the command in that directory via docker exec -w");

        // 2. Null WorkingDir ⇒ the image's default WORKDIR (alpine: `/`),
        //    i.e. NO `cd`/`-w` applied — byte-identical to the pre-feature
        //    behaviour that existing callers rely on.
        var noDir = await _provider.ExecAsync(
            _externalId!, "pwd", TimeSpan.FromSeconds(30), workingDir: null, CancellationToken.None);
        noDir.ExitCode.Should().Be(0);
        noDir.StdOut!.Trim().Should().Be("/",
            "a null WorkingDir must leave the image's default WORKDIR untouched");

        // 3. The legacy no-working-dir overload is identical to (2).
        var legacy = await _provider.ExecAsync(_externalId!, "pwd", CancellationToken.None);
        legacy.StdOut!.Trim().Should().Be(noDir.StdOut!.Trim(),
            "the new overload with null workingDir must match the legacy overload exactly");

        await _provider.DestroyContainerAsync(_externalId!, CancellationToken.None);
        _externalId = null;
    }
}
