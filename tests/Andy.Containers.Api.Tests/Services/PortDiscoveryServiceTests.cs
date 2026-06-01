using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// F6.4 (rivoli-ai/conductor#1943). PortDiscoveryService merges the provider's
// publish-on-create port mappings with a live `ss`/`netstat` listening-port
// probe run through the exec surface (no Docker-Engine verb, decision #17),
// and surfaces a suggested web-app port for Conductor's preview tab.
public class PortDiscoveryServiceTests
{
    private readonly Mock<IContainerService> _container = new();
    private readonly PortDiscoveryService _service;
    private static readonly Guid ContainerId = Guid.NewGuid();

    public PortDiscoveryServiceTests()
    {
        _service = new PortDiscoveryService(_container.Object, NullLogger<PortDiscoveryService>.Instance);
    }

    private void SetupConnection(Dictionary<int, int>? mappings)
        => _container.Setup(c => c.GetConnectionInfoAsync(ContainerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionInfo { PortMappings = mappings });

    private void SetupProbe(string stdout, int exitCode = 0)
        => _container.Setup(c => c.ExecAsync(ContainerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = exitCode, StdOut = stdout });

    // ---- ParseListeningPorts (pure) ----

    [Fact]
    public void Parse_SsOutput_ExtractsListeningPorts()
    {
        const string ss =
            "LISTEN 0      128          0.0.0.0:3000       0.0.0.0:*\n" +
            "LISTEN 0      128             [::]:8080          [::]:*\n" +
            "LISTEN 0      4096       127.0.0.1:5432       0.0.0.0:*\n";

        var ports = PortDiscoveryService.ParseListeningPorts(ss);

        ports.Should().BeEquivalentTo(new[] { 3000, 8080, 5432 });
    }

    [Fact]
    public void Parse_SsHeaderlessAndHeader_AreBothTolerated()
    {
        const string withHeader =
            "State  Recv-Q Send-Q Local Address:Port  Peer Address:Port\n" +
            "LISTEN 0      128    0.0.0.0:5173        0.0.0.0:*\n";

        PortDiscoveryService.ParseListeningPorts(withHeader).Should().BeEquivalentTo(new[] { 5173 });
    }

    [Fact]
    public void Parse_NetstatOutput_ExtractsListeningPorts()
    {
        const string netstat =
            "Active Internet connections (only servers)\n" +
            "Proto Recv-Q Send-Q Local Address           Foreign Address         State\n" +
            "tcp        0      0 0.0.0.0:8000            0.0.0.0:*               LISTEN\n" +
            "tcp6       0      0 :::4200                 :::*                    LISTEN\n";

        var ports = PortDiscoveryService.ParseListeningPorts(netstat);

        ports.Should().BeEquivalentTo(new[] { 8000, 4200 });
    }

    [Fact]
    public void Parse_EmptyOrGarbage_ReturnsEmpty()
    {
        PortDiscoveryService.ParseListeningPorts("").Should().BeEmpty();
        PortDiscoveryService.ParseListeningPorts("   \n nonsense \n").Should().BeEmpty();
    }

    // ---- GetPortsAsync ----

    [Fact]
    public async Task GetPorts_MappedAndListening_MarksListeningAndSuggestsAppPort()
    {
        SetupConnection(new Dictionary<int, int> { [3000] = 49001, [22] = 49002 });
        SetupProbe("LISTEN 0 128 0.0.0.0:3000 0.0.0.0:*\nLISTEN 0 128 0.0.0.0:22 0.0.0.0:*\n");

        var result = await _service.GetPortsAsync(ContainerId);

        result.Mapped.Should().HaveCount(2);
        result.Mapped.Single(m => m.ContainerPort == 3000).Listening.Should().BeTrue();
        result.Mapped.Single(m => m.ContainerPort == 3000).HostPort.Should().Be(49001);
        result.Mapped.Single(m => m.ContainerPort == 3000).WebEndpoint.Should().Be("http://localhost:49001");
        result.DiscoveredUnmapped.Should().BeEmpty();
        // 22 is reserved (SSH) → suggested is the real app port 3000.
        result.SuggestedAppPort.Should().Be(3000);
    }

    [Fact]
    public async Task GetPorts_DiscoveredButUnmapped_ListedSeparately()
    {
        SetupConnection(new Dictionary<int, int> { [8080] = 49010 });
        // 8080 (IDE) mapped + listening; 5173 listening but not mapped.
        SetupProbe("LISTEN 0 128 0.0.0.0:8080 0.0.0.0:*\nLISTEN 0 128 0.0.0.0:5173 0.0.0.0:*\n");

        var result = await _service.GetPortsAsync(ContainerId);

        result.Mapped.Should().ContainSingle(m => m.ContainerPort == 8080);
        result.DiscoveredUnmapped.Should().Equal(5173);
        // 8080 is reserved (IDE) and 5173 is unmapped → suggest the discovered app port.
        result.SuggestedAppPort.Should().Be(5173);
    }

    [Fact]
    public async Task GetPorts_PrefersWellKnownDevPort_OverArbitraryMapped()
    {
        SetupConnection(new Dictionary<int, int> { [9999] = 49020, [5173] = 49021 });
        SetupProbe("LISTEN 0 128 0.0.0.0:9999 0.0.0.0:*\nLISTEN 0 128 0.0.0.0:5173 0.0.0.0:*\n");

        var result = await _service.GetPortsAsync(ContainerId);

        result.SuggestedAppPort.Should().Be(5173);
    }

    [Fact]
    public async Task GetPorts_NoMappings_NoListening_EmptyOk()
    {
        SetupConnection(new Dictionary<int, int>());
        SetupProbe("");

        var result = await _service.GetPortsAsync(ContainerId);

        result.Mapped.Should().BeEmpty();
        result.DiscoveredUnmapped.Should().BeEmpty();
        result.SuggestedAppPort.Should().BeNull();
    }

    [Fact]
    public async Task GetPorts_ProbeFails_DegradesToMappedOnly()
    {
        SetupConnection(new Dictionary<int, int> { [3000] = 49001 });
        SetupProbe("", exitCode: 127); // neither ss nor netstat present

        var result = await _service.GetPortsAsync(ContainerId);

        result.Mapped.Should().ContainSingle(m => m.ContainerPort == 3000);
        result.Mapped.Single().Listening.Should().BeFalse();
        // Still suggests the mapped non-reserved port even without a live probe.
        result.SuggestedAppPort.Should().Be(3000);
    }

    [Fact]
    public async Task GetPorts_ConnectionInvalidOperation_TreatedAsEmptyMappings()
    {
        _container.Setup(c => c.GetConnectionInfoAsync(ContainerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no external id"));
        SetupProbe("LISTEN 0 128 0.0.0.0:5173 0.0.0.0:*\n");

        var result = await _service.GetPortsAsync(ContainerId);

        result.Mapped.Should().BeEmpty();
        result.DiscoveredUnmapped.Should().Equal(5173);
        result.SuggestedAppPort.Should().Be(5173);
    }

    [Fact]
    public async Task GetPorts_ProbeThrowsInvalidOperation_DegradesToMappedOnly()
    {
        SetupConnection(new Dictionary<int, int> { [3000] = 49001 });
        _container.Setup(c => c.ExecAsync(ContainerId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Container is Stopped, cannot exec"));

        var result = await _service.GetPortsAsync(ContainerId);

        result.Mapped.Should().ContainSingle(m => m.ContainerPort == 3000);
        result.Mapped.Single().Listening.Should().BeFalse();
    }

    [Fact]
    public async Task ExposePort_DelegatesToContainerService()
    {
        _container.Setup(c => c.ExposePortAsync(ContainerId, 3000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MappedPort { ContainerPort = 3000, HostPort = 49100, Listening = true });

        var mapped = await _service.ExposePortAsync(ContainerId, 3000);

        mapped.HostPort.Should().Be(49100);
        _container.Verify(c => c.ExposePortAsync(ContainerId, 3000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExposePort_UnsupportedProvider_PropagatesNotSupported()
    {
        _container.Setup(c => c.ExposePortAsync(ContainerId, 3000, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("cannot publish on running container"));

        var act = () => _service.ExposePortAsync(ContainerId, 3000);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
