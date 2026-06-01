// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Providers.Local;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Integration.Tests;

/// <summary>
/// F6.4 (rivoli-ai/conductor#1943). End-to-end web-port preview against a REAL
/// Docker container: create a container with a published app port, start a tiny
/// HTTP server inside it, then assert (a) <see cref="PortDiscoveryService"/>
/// discovers the listening port via the exec surface (`ss`/`netstat` — no
/// Docker-Engine verb, decision #17) and reports the mapped host port, and (b)
/// that host port actually serves HTTP 200 over loopback (the same reach
/// Conductor's preview uses through the UnifiedProxy). Also asserts the
/// unsupported-provider expose path returns NotSupportedException.
///
/// Requires: Docker daemon running.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Docker")]
public class PortDiscoveryIntegrationTests : IAsyncLifetime
{
    private readonly DockerInfrastructureProvider _provider;
    private string? _externalId;
    private int _hostPort;
    private const int AppPort = 8000;

    public PortDiscoveryIntegrationTests()
    {
        _provider = new DockerInfrastructureProvider(
            null, NullLoggerFactory.Instance.CreateLogger<DockerInfrastructureProvider>());
    }

    public async Task InitializeAsync()
    {
        // Publish the container's app port to an ephemeral host port at
        // create-time (Docker only supports publish-on-create).
        _hostPort = GetFreeTcpPort();
        var spec = new ContainerSpec
        {
            Name = $"webport-it-{Guid.NewGuid().ToString()[..8]}",
            ImageReference = "python:3.12-alpine",
            Resources = new ResourceSpec { CpuCores = 1, MemoryMb = 256 },
            PortMappings = new Dictionary<int, int> { [AppPort] = _hostPort },
            // Keep the container alive; the test starts the HTTP server via exec.
            Command = "sleep",
            Arguments = new[] { "infinity" },
        };
        var created = await _provider.CreateContainerAsync(spec, CancellationToken.None);
        _externalId = created.ExternalId;

        // Start a tiny HTTP server bound to 0.0.0.0:8000 inside the container.
        await Exec($"mkdir -p /srv && printf 'hello-from-run\\n' > /srv/index.html");
        await ExecDetached($"cd /srv && (python3 -m http.server {AppPort} >/tmp/httpd.log 2>&1 &)");

        // Give the server a moment to bind.
        await WaitUntilListeningAsync(AppPort, TimeSpan.FromSeconds(15));
    }

    public async Task DisposeAsync()
    {
        if (_externalId is not null)
        {
            try { await _provider.DestroyContainerAsync(_externalId, CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    private PortDiscoveryService NewService()
    {
        var containerService = new SingleContainerExecAdapter(_provider, _externalId!);
        return new PortDiscoveryService(containerService, NullLogger<PortDiscoveryService>.Instance);
    }

    [Fact]
    public async Task GetPorts_DiscoversListeningPort_AndHostPortServes200()
    {
        var service = NewService();

        var result = await service.GetPortsAsync(Guid.NewGuid(), CancellationToken.None);

        // The published app port is mapped, listening, and suggested.
        var mapped = result.Mapped.Should().ContainSingle(m => m.ContainerPort == AppPort).Subject;
        mapped.HostPort.Should().Be(_hostPort);
        mapped.Listening.Should().BeTrue("the http.server is bound inside the container");
        result.SuggestedAppPort.Should().Be(AppPort);

        // The mapped host port actually serves over loopback — the same reach
        // Conductor's embedded preview uses via the UnifiedProxy.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.GetAsync($"http://localhost:{_hostPort}/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("hello-from-run");
    }

    [Fact]
    public async Task ExposePort_OnRunningDockerContainer_ThrowsNotSupported()
    {
        // Docker cannot add a mapping to a running container → NotSupported
        // (surfaced as 400 by the controller).
        var act = () => _provider.ExposePortAsync(_externalId!, 9999, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private async Task Exec(string cmd)
    {
        var r = await _provider.ExecAsync(_externalId!, cmd, CancellationToken.None);
        r.ExitCode.Should().Be(0, $"setup command failed: {r.StdErr}");
    }

    // Fire-and-forget exec (backgrounded process); don't assert exit code.
    private async Task ExecDetached(string cmd)
        => await _provider.ExecAsync(_externalId!, cmd, CancellationToken.None);

    private async Task WaitUntilListeningAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var r = await _provider.ExecAsync(_externalId!,
                "(command -v ss >/dev/null 2>&1 && ss -ltnH || netstat -ltn) 2>/dev/null", CancellationToken.None);
            if ((r.StdOut ?? string.Empty).Contains($":{port}")) return;
            await Task.Delay(500);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Thin <see cref="IContainerService"/> mapping any container id onto a single
/// real Docker container's external id — exercises the real
/// PortDiscoveryService → ConnectionInfo + exec(ss) chain.
/// </summary>
internal sealed class SingleContainerExecAdapter : IContainerService
{
    private readonly IInfrastructureProvider _provider;
    private readonly string _externalId;

    public SingleContainerExecAdapter(IInfrastructureProvider provider, string externalId)
    {
        _provider = provider;
        _externalId = externalId;
    }

    public Task<ExecResult> ExecAsync(Guid containerId, string command, CancellationToken ct = default)
        => _provider.ExecAsync(_externalId, command, ct);

    public Task<ExecResult> ExecAsync(Guid containerId, string command, TimeSpan timeout, CancellationToken ct = default)
        => _provider.ExecAsync(_externalId, command, timeout, ct);

    public Task<ConnectionInfo> GetConnectionInfoAsync(Guid containerId, CancellationToken ct = default)
        => _provider.GetConnectionInfoAsync(_externalId, ct);

    public Task<MappedPort> ExposePortAsync(Guid containerId, int containerPort, CancellationToken ct = default)
        => _provider.ExposePortAsync(_externalId, containerPort, ct);

    public Task<Container> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Container> GetContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Container>> ListContainersAsync(ContainerFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
    public Task StartContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task StopContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DestroyContainerAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ContainerStats> GetContainerStatsAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ResizeContainerAsync(Guid containerId, ResourceSpec resources, CancellationToken ct = default) => throw new NotSupportedException();
}
