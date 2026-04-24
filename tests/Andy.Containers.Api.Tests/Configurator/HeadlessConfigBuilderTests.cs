using Andy.Containers.Configurator;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Configurator;

// AP3 (rivoli-ai/andy-containers#105). Verifies the Run + AgentSpec ->
// HeadlessRunConfig mapper enforces the AQ1 schema closures (provider enum,
// transport oneOf, required string mins) up-front so AP6 never has to
// load-and-reject a config it just wrote.
public class HeadlessConfigBuilderTests
{
    private readonly HeadlessConfigBuilder _builder = new();

    [Fact]
    public void Build_HappyPath_ProducesSchemaConformingConfig()
    {
        var run = SeedRun();
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.SchemaVersion.Should().Be(1);
        config.RunId.Should().Be(run.Id);

        config.Agent.Slug.Should().Be("triage-agent");
        config.Agent.Revision.Should().Be(3);
        config.Agent.Instructions.Should().NotBeNullOrWhiteSpace();
        config.Agent.OutputFormat.Should().Be("json-triage-output-v1");

        config.Model.Provider.Should().Be("anthropic");
        config.Model.Id.Should().Be("claude-sonnet-4-6");
        config.Model.ApiKeyRef.Should().Be("env:ANDY_MODEL_KEY");

        config.Tools.Should().HaveCount(2);
        config.Tools[0].Transport.Should().Be("mcp");
        config.Tools[0].Endpoint.Should().Be("https://mcp.internal/tools/issues.get");
        config.Tools[1].Transport.Should().Be("cli");
        config.Tools[1].Binary.Should().Be("andy-issues-cli");
        config.Tools[1].Command.Should().Equal("andy-issues-cli", "search");

        config.Workspace.Root.Should().Be("/workspace");
        config.Workspace.Branch.Should().Be("main", "branch flows from Run.WorkspaceRef.Branch");

        config.Output.File.Should().Be("/workspace/.andy-run/output.json");
        config.Output.Stream.Should().Be("stdout");

        config.EventSink!.NatsSubject.Should().Be(
            $"andy.containers.events.run.{run.Id}.progress",
            "subject must match the schema pattern andy.containers.events.run.{uuid}.{event}");

        config.PolicyId.Should().Be(run.PolicyId);
        config.Boundaries.Should().Equal("read-only");
        config.Limits.MaxIterations.Should().Be(50);
        config.Limits.TimeoutSeconds.Should().Be(300);
    }

    [Fact]
    public void Build_EmptyInstructions_Throws()
    {
        var run = SeedRun();
        var spec = TriageAgent() with { Instructions = "   " };

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Instructions*", "schema requires minLength 1");
    }

    [Theory]
    [InlineData("aws-bedrock")]
    [InlineData("")]
    [InlineData("ANTHROPIC")]
    public void Build_UnknownProvider_Throws(string provider)
    {
        var run = SeedRun();
        var spec = TriageAgent() with
        {
            Model = new AgentSpecModel { Provider = provider, Id = "claude-sonnet-4-6" },
        };

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Provider*", "schema enum closes at anthropic|openai|google|cerebras|local");
    }

    [Fact]
    public void Build_McpToolMissingEndpoint_Throws()
    {
        var run = SeedRun();
        var spec = TriageAgent() with
        {
            Tools = new[]
            {
                new AgentSpecTool { Name = "issues.get", Transport = "mcp", Endpoint = null },
            },
        };

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Endpoint*");
    }

    [Fact]
    public void Build_CliToolMissingBinary_Throws()
    {
        var run = SeedRun();
        var spec = TriageAgent() with
        {
            Tools = new[]
            {
                new AgentSpecTool { Name = "git", Transport = "cli", Binary = null },
            },
        };

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Binary*");
    }

    [Fact]
    public void Build_UnknownTransport_Throws()
    {
        var run = SeedRun();
        var spec = TriageAgent() with
        {
            Tools = new[]
            {
                new AgentSpecTool { Name = "weird", Transport = "grpc" },
            },
        };

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*transport*");
    }

    [Fact]
    public void Build_EmptyEnvVarsCollapseToNull()
    {
        var run = SeedRun();
        var spec = TriageAgent() with { EnvVars = new Dictionary<string, string>() };

        var config = _builder.Build(run, spec);

        config.EnvVars.Should().BeNull(
            "schema permits omission; emitting an empty object adds noise to the on-disk config");
    }

    [Fact]
    public void Build_EmptyBoundariesCollapseToNull()
    {
        var run = SeedRun();
        var spec = TriageAgent() with { Boundaries = Array.Empty<string>() };

        var config = _builder.Build(run, spec);

        config.Boundaries.Should().BeNull();
    }

    [Fact]
    public void Build_RunWithoutId_Throws()
    {
        var run = SeedRun();
        run.Id = Guid.Empty;
        var spec = TriageAgent();

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>().WithMessage("*Run.Id*");
    }

    private static Run SeedRun() => new()
    {
        Id = Guid.NewGuid(),
        AgentId = "triage-agent",
        AgentRevision = 3,
        Mode = RunMode.Headless,
        EnvironmentProfileId = Guid.NewGuid(),
        WorkspaceRef = new WorkspaceRef { WorkspaceId = Guid.NewGuid(), Branch = "main" },
        PolicyId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
    };

    private static AgentSpec TriageAgent() => new()
    {
        Slug = "triage-agent",
        Revision = 3,
        Instructions = "You are the triage agent.",
        OutputFormat = "json-triage-output-v1",
        Model = new AgentSpecModel
        {
            Provider = "anthropic",
            Id = "claude-sonnet-4-6",
            ApiKeyRef = "env:ANDY_MODEL_KEY",
        },
        Tools = new[]
        {
            new AgentSpecTool { Name = "issues.get", Transport = "mcp", Endpoint = "https://mcp.internal/tools/issues.get" },
            new AgentSpecTool
            {
                Name = "repo.search",
                Transport = "cli",
                Binary = "andy-issues-cli",
                Command = new[] { "andy-issues-cli", "search" },
            },
        },
        Boundaries = new[] { "read-only" },
        Limits = new AgentSpecLimits { MaxIterations = 50, TimeoutSeconds = 300 },
    };
}
