using Andy.Containers.Configurator;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Configurator;

// AP3 (rivoli-ai/andy-containers#105). Verifies the orchestrator returns
// structured failure (rather than throwing) when any of the three legs fail,
// so the AP2 controller can log + continue without rolling the Run back.
// AP10 (#112) extends with run-scoped token injection; tests for that path
// verify env-var merging, agent override semantics, and idempotent mint.
public class RunConfiguratorTests
{
    private readonly Mock<IAndyAgentsClient> _agents = new();
    private readonly Mock<IHeadlessConfigBuilder> _builder = new();
    private readonly Mock<IHeadlessConfigWriter> _writer = new();
    private readonly Mock<ITokenIssuer> _tokens = new();
    private readonly SecretsOptions _secretsOptions = new()
    {
        ProxyUrl = "https://proxy.test",
        McpUrl = "https://mcp.test",
    };

    public RunConfiguratorTests()
    {
        // Default mint stub returns a deterministic token per runId so tests
        // that don't care about token contents still get a non-null env var.
        _tokens
            .Setup(t => t.MintAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid runId, CancellationToken _) =>
                new RunToken($"andy-run.test-{runId}", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private RunConfigurator CreateSut() =>
        new(_agents.Object, _builder.Object, _writer.Object,
            _tokens.Object, Options.Create(_secretsOptions),
            NullLogger<RunConfigurator>.Instance);

    [Fact]
    public async Task ConfigureAsync_HappyPath_WritesAndReturnsPath()
    {
        var run = SeedRun();
        var spec = TriageAgent();
        var config = SampleConfig(run.Id);
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spec);
        // AP10 (#112): configurator now augments the AgentSpec's EnvVars before
        // calling the builder, so the builder receives a different (with-cloned)
        // record. Match on any AgentSpec rather than the original instance.
        _builder.Setup(b => b.Build(run, It.IsAny<AgentSpec>())).Returns(config);
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
        _builder.Setup(b => b.Build(run, It.IsAny<AgentSpec>())).Throws(new ArgumentException("bad provider"));

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
        _builder.Setup(b => b.Build(run, It.IsAny<AgentSpec>())).Returns(config);
        _writer.Setup(w => w.WriteAsync(config, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var result = await CreateSut().ConfigureAsync(run);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("disk full");
    }

    // AP10 (#112) ----------------------------------------------------------

    [Fact]
    public async Task ConfigureAsync_InjectsRunScopedToken_AndUrlsIntoEnvVars()
    {
        var run = SeedRun();
        var spec = TriageAgent();
        AgentSpec? captured = null;
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spec);
        _builder
            .Setup(b => b.Build(run, It.IsAny<AgentSpec>()))
            .Callback<Run, AgentSpec>((_, s) => captured = s)
            .Returns(SampleConfig(run.Id));
        _writer.Setup(w => w.WriteAsync(It.IsAny<HeadlessRunConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/x/config.json");

        var result = await CreateSut().ConfigureAsync(run);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.EnvVars.Should().NotBeNull();
        captured.EnvVars![EnvVarNames.AndyToken].Should().Be($"andy-run.test-{run.Id}");
        captured.EnvVars[EnvVarNames.AndyProxyUrl].Should().Be("https://proxy.test");
        captured.EnvVars[EnvVarNames.AndyMcpUrl].Should().Be("https://mcp.test");

        _tokens.Verify(t => t.MintAsync(run.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigureAsync_AgentEnvVarsWinOnCollision()
    {
        // An agent author can pin their own ANDY_TOKEN (e.g. a test
        // double or integration fixture). The platform must not silently
        // overwrite it — the collision is the agent's call, not the
        // configurator's.
        var run = SeedRun();
        var spec = TriageAgent() with
        {
            EnvVars = new Dictionary<string, string>
            {
                [EnvVarNames.AndyToken] = "agent-pinned",
                ["UNRELATED"] = "kept",
            },
        };
        AgentSpec? captured = null;
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spec);
        _builder
            .Setup(b => b.Build(run, It.IsAny<AgentSpec>()))
            .Callback<Run, AgentSpec>((_, s) => captured = s)
            .Returns(SampleConfig(run.Id));
        _writer.Setup(w => w.WriteAsync(It.IsAny<HeadlessRunConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/x/config.json");

        await CreateSut().ConfigureAsync(run);

        captured!.EnvVars![EnvVarNames.AndyToken].Should().Be("agent-pinned");
        captured.EnvVars["UNRELATED"].Should().Be("kept");
        captured.EnvVars[EnvVarNames.AndyProxyUrl].Should().Be("https://proxy.test",
            "platform vars the agent didn't pin still flow through");
    }

    [Fact]
    public async Task ConfigureAsync_NullSecretsOptions_OmitsUrlVars_ButStillInjectsToken()
    {
        // A half-configured deployment (no Secrets section) must not
        // emit empty-string env vars — that would break agents that
        // sniff for these vars and try to dial 'http://'. Token always
        // ships because the issuer is mandatory.
        _secretsOptions.ProxyUrl = null;
        _secretsOptions.McpUrl = null;
        var run = SeedRun();
        var spec = TriageAgent();
        AgentSpec? captured = null;
        _agents.Setup(a => a.GetAgentAsync(run.AgentId, run.AgentRevision, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spec);
        _builder
            .Setup(b => b.Build(run, It.IsAny<AgentSpec>()))
            .Callback<Run, AgentSpec>((_, s) => captured = s)
            .Returns(SampleConfig(run.Id));
        _writer.Setup(w => w.WriteAsync(It.IsAny<HeadlessRunConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/tmp/x/config.json");

        await CreateSut().ConfigureAsync(run);

        captured!.EnvVars!.Should().ContainKey(EnvVarNames.AndyToken);
        captured.EnvVars.Should().NotContainKey(EnvVarNames.AndyProxyUrl,
            "missing proxy URL must surface as a missing var, not an empty one");
        captured.EnvVars.Should().NotContainKey(EnvVarNames.AndyMcpUrl);
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
