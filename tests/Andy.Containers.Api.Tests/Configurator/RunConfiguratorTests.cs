using Andy.Containers.Configurator;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Configurator;

// AP3 (rivoli-ai/andy-containers#105). Verifies the orchestrator returns
// structured failure (rather than throwing) when any of the three legs fail,
// so the AP2 controller can log + continue without rolling the Run back.
public class RunConfiguratorTests
{
    private readonly Mock<IAndyAgentsClient> _agents = new();
    private readonly Mock<IHeadlessConfigBuilder> _builder = new();
    private readonly Mock<IHeadlessConfigWriter> _writer = new();

    private RunConfigurator CreateSut() =>
        new(_agents.Object, _builder.Object, _writer.Object, NullLogger<RunConfigurator>.Instance);

    [Fact]
    public async Task ConfigureAsync_HappyPath_WritesAndReturnsPath()
    {
        var run = SeedRun();
        var spec = TriageAgent();
        var config = SampleConfig(run.Id);
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spec);
        _builder.Setup(b => b.Build(run, spec)).Returns(config);
        _writer.Setup(w => w.WriteAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/runs/x/config.json");

        var result = await CreateSut().ConfigureAsync(run);

        result.IsSuccess.Should().BeTrue();
        result.Path.Should().Be("/tmp/runs/x/config.json");
    }

    [Fact]
    public async Task ConfigureAsync_AgentNotFound_ReturnsFailure()
    {
        var run = SeedRun();
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSpec?)null);

        var result = await CreateSut().ConfigureAsync(run);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(run.AgentId);
        _builder.VerifyNoOtherCalls();
        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfigureAsync_BuilderRejects_ReturnsFailure()
    {
        var run = SeedRun();
        var spec = TriageAgent();
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spec);
        _builder.Setup(b => b.Build(run, spec)).Throws(new ArgumentException("bad provider"));

        var result = await CreateSut().ConfigureAsync(run);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("bad provider");
        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfigureAsync_WriterIoError_ReturnsFailure()
    {
        var run = SeedRun();
        var spec = TriageAgent();
        var config = SampleConfig(run.Id);
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spec);
        _builder.Setup(b => b.Build(run, spec)).Returns(config);
        _writer.Setup(w => w.WriteAsync(config, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var result = await CreateSut().ConfigureAsync(run);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("disk full");
    }

    private static Run SeedRun() => new()
    {
        Id = Guid.NewGuid(),
        AgentId = "triage-agent",
        AgentRevision = 1,
        Mode = RunMode.Headless,
        EnvironmentProfileId = Guid.NewGuid(),
        WorkspaceRef = new WorkspaceRef { WorkspaceId = Guid.NewGuid(), Branch = "main" },
        CorrelationId = Guid.NewGuid(),
    };

    private static AgentSpec TriageAgent() => new()
    {
        Slug = "triage-agent",
        Instructions = "...",
        Model = new AgentSpecModel { Provider = "anthropic", Id = "claude-sonnet-4-6" },
        Limits = new AgentSpecLimits { MaxIterations = 10, TimeoutSeconds = 60 },
    };

    private static HeadlessRunConfig SampleConfig(Guid runId) => new()
    {
        RunId = runId,
        Agent = new HeadlessAgent { Slug = "triage-agent", Instructions = "..." },
        Model = new HeadlessModel { Provider = "anthropic", Id = "claude-sonnet-4-6" },
        Workspace = new HeadlessWorkspace { Root = "/workspace" },
        Output = new HeadlessOutput { File = "/workspace/.andy-run/output.json", Stream = "stdout" },
        Limits = new HeadlessLimits { MaxIterations = 10, TimeoutSeconds = 60 },
    };
}
