using Andy.Containers.Configurator;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Containers.Api.Tests.Configurator;

// AP12 (rivoli-ai/andy-containers#115). The field-by-field tests in
// HeadlessConfigBuilderTests verify individual property mappings, but
// they would silently miss:
//
// - a new field landing on HeadlessRunConfig (type drift away from AQ3),
// - an existing field renamed (snake_case typo),
// - a JSON option flip (e.g. WriteIndented off),
// - a nullable field that quietly stops being elided.
//
// Golden files capture the full serialised output so any of these
// changes break a test instead of slipping past review. When CI fails
// here, inspect the diff and either fix the code or update the
// snapshot — never skip the test.
//
// Snapshots live under __snapshots__/ next to this file. They use
// deterministic Guids so the comparison is byte-stable across runs.
public class HeadlessConfigBuilderGoldenFileTests
{
    private static readonly string SnapshotDir = Path.Combine(
        AppContext.BaseDirectory, "Configurator", "__snapshots__");

    private readonly HeadlessConfigBuilder _builder = new();
    private readonly ITestOutputHelper _output;

    public HeadlessConfigBuilderGoldenFileTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("triage")]
    [InlineData("plan")]
    [InlineData("execute")]
    public void Build_ProducesSnapshotMatchingFixture(string agentType)
    {
        var (run, spec) = agentType switch
        {
            "triage" => (TriageRun(), TriageSpec()),
            "plan" => (PlanRun(), PlanSpec()),
            "execute" => (ExecuteRun(), ExecuteSpec()),
            _ => throw new ArgumentException($"Unknown agent type {agentType}"),
        };

        var config = _builder.Build(run, spec);
        var actual = HeadlessConfigJson.Serialize(config);

        var snapshotPath = Path.Combine(SnapshotDir, $"{agentType}.json");

        if (!File.Exists(snapshotPath))
        {
            // First run for this fixture: write the snapshot and fail
            // so the dev sees the file appear and can review the
            // initial shape before committing it. Better than silently
            // accepting whatever the builder happened to produce.
            Directory.CreateDirectory(SnapshotDir);
            File.WriteAllText(snapshotPath, actual);
            _output.WriteLine(
                $"Snapshot did not exist; wrote initial fixture to {snapshotPath}. " +
                "Inspect, commit, and rerun.");
            Assert.Fail("Snapshot was missing; an initial fixture has been written. Review it and rerun.");
        }

        var expected = File.ReadAllText(snapshotPath);

        // Normalise line endings — git autocrlf and editor settings can
        // flip CRLF/LF without anyone noticing; pinning to LF keeps
        // CI behaviour identical between Linux runners and developer
        // machines on Windows.
        actual = actual.Replace("\r\n", "\n");
        expected = expected.Replace("\r\n", "\n");

        actual.Should().Be(expected,
            $"the serialised HeadlessRunConfig must match {agentType}.json byte-for-byte. " +
            "If this is an intentional change, update the fixture.");
    }

    // Deterministic ids so the JSON serialisation is byte-stable.
    private static readonly Guid TriageRunId       = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    private static readonly Guid TriageWorkspaceId = Guid.Parse("0a000000-0000-0000-0000-000000000002");
    private static readonly Guid TriagePolicyId    = Guid.Parse("0a000000-0000-0000-0000-000000000003");
    private static readonly Guid TriageProfileId   = Guid.Parse("0a000000-0000-0000-0000-000000000004");
    private static readonly Guid TriageCorrelation = Guid.Parse("0a000000-0000-0000-0000-000000000005");

    private static Run TriageRun() => new()
    {
        Id = TriageRunId,
        AgentId = "triage-agent",
        AgentRevision = 3,
        Mode = RunMode.Headless,
        EnvironmentProfileId = TriageProfileId,
        WorkspaceRef = new WorkspaceRef { WorkspaceId = TriageWorkspaceId, Branch = "main" },
        PolicyId = TriagePolicyId,
        CorrelationId = TriageCorrelation,
    };

    private static AgentSpec TriageSpec() => new()
    {
        Slug = "triage-agent",
        Revision = 3,
        Instructions = "Read the issue, classify it, and emit a triage record.",
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
        Limits = new AgentSpecLimits { MaxIterations = 25, TimeoutSeconds = 180 },
    };

    private static readonly Guid PlanRunId       = Guid.Parse("0b000000-0000-0000-0000-000000000001");
    private static readonly Guid PlanWorkspaceId = Guid.Parse("0b000000-0000-0000-0000-000000000002");
    private static readonly Guid PlanProfileId   = Guid.Parse("0b000000-0000-0000-0000-000000000004");
    private static readonly Guid PlanCorrelation = Guid.Parse("0b000000-0000-0000-0000-000000000005");

    private static Run PlanRun() => new()
    {
        Id = PlanRunId,
        AgentId = "planning-agent",
        AgentRevision = 7,
        Mode = RunMode.Headless,
        EnvironmentProfileId = PlanProfileId,
        WorkspaceRef = new WorkspaceRef { WorkspaceId = PlanWorkspaceId, Branch = "feature/x" },
        PolicyId = null,
        CorrelationId = PlanCorrelation,
    };

    private static AgentSpec PlanSpec() => new()
    {
        Slug = "planning-agent",
        Revision = 7,
        Instructions = "Decompose the user story into ordered steps.",
        OutputFormat = "json-plan-v1",
        Model = new AgentSpecModel
        {
            Provider = "openai",
            Id = "gpt-4o",
            // Deliberately no ApiKeyRef — exercises the optional-field elision.
        },
        Tools = new[]
        {
            new AgentSpecTool { Name = "tasks.create", Transport = "mcp", Endpoint = "https://mcp.internal/tools/tasks.create" },
        },
        // No boundaries / no env vars — exercises the empty-collection-collapses-to-null path.
        Limits = new AgentSpecLimits { MaxIterations = 10, TimeoutSeconds = 120 },
    };

    private static readonly Guid ExecuteRunId       = Guid.Parse("0c000000-0000-0000-0000-000000000001");
    private static readonly Guid ExecuteWorkspaceId = Guid.Parse("0c000000-0000-0000-0000-000000000002");
    private static readonly Guid ExecutePolicyId    = Guid.Parse("0c000000-0000-0000-0000-000000000003");
    private static readonly Guid ExecuteProfileId   = Guid.Parse("0c000000-0000-0000-0000-000000000004");
    private static readonly Guid ExecuteCorrelation = Guid.Parse("0c000000-0000-0000-0000-000000000005");

    private static Run ExecuteRun() => new()
    {
        Id = ExecuteRunId,
        AgentId = "execute-agent",
        AgentRevision = 11,
        Mode = RunMode.Headless,
        EnvironmentProfileId = ExecuteProfileId,
        WorkspaceRef = new WorkspaceRef { WorkspaceId = ExecuteWorkspaceId, Branch = "main" },
        PolicyId = ExecutePolicyId,
        CorrelationId = ExecuteCorrelation,
    };

    private static AgentSpec ExecuteSpec() => new()
    {
        Slug = "execute-agent",
        Revision = 11,
        Instructions = "Carry out the next step in the plan and report results.",
        OutputFormat = "json-execute-v1",
        Model = new AgentSpecModel
        {
            Provider = "anthropic",
            Id = "claude-opus-4-7",
            ApiKeyRef = "env:ANDY_MODEL_KEY",
        },
        Tools = new[]
        {
            new AgentSpecTool { Name = "fs.read", Transport = "mcp", Endpoint = "https://mcp.internal/tools/fs.read" },
            new AgentSpecTool { Name = "fs.write", Transport = "mcp", Endpoint = "https://mcp.internal/tools/fs.write" },
            new AgentSpecTool
            {
                Name = "git",
                Transport = "cli",
                Binary = "/usr/bin/git",
                Command = new[] { "git" },
            },
        },
        EnvVars = new Dictionary<string, string>
        {
            ["ANDY_AUDIT_MODE"] = "Strict",
            ["ANDY_PROXY_URL"] = "https://proxy.andy.local",
        },
        Boundaries = new[] { "branch:feature/*", "fs:/workspace/**" },
        Limits = new AgentSpecLimits { MaxIterations = 100, TimeoutSeconds = 900 },
    };
}
