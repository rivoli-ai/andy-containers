using Andy.Containers.Abstractions;
using Andy.Containers.Infrastructure.Providers.Apple;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// Integration tests for AppleContainerProvider.
/// Requires: macOS with Apple Silicon, `container` CLI installed,
/// and `container system start` already run.
/// These tests create/start/stop/destroy real containers.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AppleContainer")]
public class AppleContainerProviderTests : IAsyncLifetime
{
    private readonly AppleContainerProvider _provider;
    private readonly string _testContainerName = $"integration-test-{Guid.NewGuid().ToString()[..8]}";
    private string? _externalId;

    public AppleContainerProviderTests()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<AppleContainerProvider>();
        _provider = new AppleContainerProvider(null, logger);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Best-effort cleanup
        if (_externalId is not null)
        {
            try { await _provider.DestroyContainerAsync(_externalId, CancellationToken.None); }
            catch { /* ignore cleanup failures */ }
        }
    }

    [Fact]
    public async Task HealthCheck_WhenServiceRunning_ShouldReturnHealthy()
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
            Name = _testContainerName,
            ImageReference = "ubuntu:24.04",
            Resources = new ResourceSpec { CpuCores = 2, MemoryMb = 512 }
        };

        var result = await _provider.CreateContainerAsync(spec, CancellationToken.None);

        result.Should().NotBeNull();
        result.ExternalId.Should().NotBeNullOrEmpty();
        result.Status.Should().Be(ContainerStatus.Running);
        _externalId = result.ExternalId;

        // 2. Verify connection info has an IP address
        var connInfo = await _provider.GetConnectionInfoAsync(_externalId, CancellationToken.None);
        connInfo.IpAddress.Should().NotBeNullOrEmpty("container should have a network address");

        // 3. Verify container info shows running
        var info = await _provider.GetContainerInfoAsync(_externalId, CancellationToken.None);
        info.Status.Should().Be(ContainerStatus.Running);
        info.IpAddress.Should().NotBeNullOrEmpty();

        // 4. Execute a command inside the container
        var execResult = await _provider.ExecAsync(_externalId, "echo hello-from-container", CancellationToken.None);
        execResult.ExitCode.Should().Be(0);
        execResult.StdOut.Should().Contain("hello-from-container");

        // 5. Execute a command that produces stderr
        var failResult = await _provider.ExecAsync(_externalId, "ls /nonexistent", CancellationToken.None);
        failResult.ExitCode.Should().NotBe(0);
        failResult.StdErr.Should().NotBeNullOrEmpty();

        // 6. Stop the container
        await _provider.StopContainerAsync(_externalId, CancellationToken.None);

        var stoppedInfo = await _provider.GetContainerInfoAsync(_externalId, CancellationToken.None);
        stoppedInfo.Status.Should().Be(ContainerStatus.Stopped);

        // 7. Destroy the container
        await _provider.DestroyContainerAsync(_externalId, CancellationToken.None);
        _externalId = null; // already cleaned up
    }

    [Fact]
    public async Task GetCapabilities_ShouldReturnAppleContainerCapabilities()
    {
        var caps = await _provider.GetCapabilitiesAsync(CancellationToken.None);

        caps.Type.Should().Be(ProviderType.AppleContainer);
        caps.SupportedArchitectures.Should().Contain("arm64");
        caps.SupportsExec.Should().BeTrue();
        caps.SupportsPortForwarding.Should().BeFalse();
    }
}
