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
    public void Build_WithObjective_BakesTaskIntoSystemPrompt()
    {
        // andy-cli uses Agent.Instructions as the system prompt + a fixed
        // "Begin." kickoff, so the concrete task must be inside the
        // instructions or the agent has nothing to act on. The per-run
        // objective (forwarded from the andy-tasks delegation contract) must
        // be appended to the agent's generic role prompt.
        var run = SeedRun();
        run.Objective = "Add a Quick Start section to README.md.";
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Agent.Instructions.Should().Contain(spec.Instructions,
            "the generic role prompt is preserved");
        config.Agent.Instructions.Should().Contain("Add a Quick Start section to README.md.",
            "the concrete task objective must be baked into the system prompt");
    }

    [Fact]
    public void Build_WithoutObjective_LeavesRoleInstructionsUnchanged()
    {
        // A run with no objective (read-only role, or a caller that didn't
        // forward one) keeps exactly the role prompt — no trailing scaffolding.
        var run = SeedRun(); // Objective null
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Agent.Instructions.Should().Be(spec.Instructions);
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
            .WithMessage("*Provider*",
                "schema enum closes at anthropic|openai|openrouter|google|cerebras|groq|local");
    }

    // Every provider in the andy-cli headless-config.v1 schema enum must BUILD
    // (no producer-vs-schema drift). openrouter + groq regressed here while
    // present in the schema, breaking the OpenRouter setup at config-build time.
    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    [InlineData("openrouter")]
    [InlineData("google")]
    [InlineData("cerebras")]
    [InlineData("groq")]
    [InlineData("local")]
    public void Build_SchemaEnumProvider_Succeeds(string provider)
    {
        var run = SeedRun();
        var spec = TriageAgent() with
        {
            Model = new AgentSpecModel { Provider = provider, Id = "some-model-id" },
        };

        var config = _builder.Build(run, spec);

        config.Model.Provider.Should().Be(provider,
            "every provider in the schema's model.provider enum must be accepted by the builder");
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

    // ----- EX.7 (rivoli-ai/andy-containers#328) inputs mapping -----

    [Fact]
    public void Build_NoInputs_OmitsInputsAndSerializesWithoutKey()
    {
        var run = SeedRun();
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Inputs.Should().BeNull("a run without inputs carries no inputs section");

        var json = HeadlessConfigJson.Serialize(config);
        json.Should().NotContain("\"inputs\"",
            "the WhenWritingNull policy must drop the absent inputs key entirely");
    }

    [Fact]
    public void Build_EmptyInputsCollapseToNull()
    {
        var run = SeedRun();
        run.Inputs = Array.Empty<RunInput>();
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Inputs.Should().BeNull(
            "empty inputs collapse to null, matching env_vars/boundaries posture");
    }

    [Fact]
    public void Build_WithInputs_MapsAndSerializesSnakeCase()
    {
        var run = SeedRun();
        var docA = Guid.NewGuid();
        var docB = Guid.NewGuid();
        run.Inputs = new[]
        {
            new RunInput(docA, "prior/report.json"),
            new RunInput(docB, "context.md"),
        };
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Inputs.Should().HaveCount(2);
        config.Inputs![0].DocsRef.Should().Be(docA);
        config.Inputs[0].DestRelativePath.Should().Be("prior/report.json");
        config.Inputs[1].DocsRef.Should().Be(docB);
        config.Inputs[1].DestRelativePath.Should().Be("context.md");

        // Round-trips through the configurator's snake_case serializer with
        // the AQ1 wire names docs_ref / dest_relative_path.
        var json = HeadlessConfigJson.Serialize(config);
        json.Should().Contain("\"inputs\"");
        json.Should().Contain("\"docs_ref\"");
        json.Should().Contain("\"dest_relative_path\"");
        json.Should().Contain(docA.ToString());

        // And deserializes back into an equivalent shape.
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<HeadlessRunConfig>(
            json, HeadlessConfigJson.Options);
        roundTripped!.Inputs.Should().HaveCount(2);
        roundTripped.Inputs![0].DocsRef.Should().Be(docA);
        roundTripped.Inputs[0].DestRelativePath.Should().Be("prior/report.json");
    }

    [Fact]
    public void Build_InputWithEmptyDocsRef_Throws()
    {
        var run = SeedRun();
        run.Inputs = new[] { new RunInput(Guid.Empty, "x.json") };
        var spec = TriageAgent();

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>().WithMessage("*DocsRef*");
    }

    [Theory]
    [InlineData("/etc/passwd")]            // absolute path
    [InlineData("../escape.txt")]          // leading traversal
    [InlineData("a/../../b.txt")]          // mid-path traversal
    [InlineData("..")]                     // bare traversal
    [InlineData("C:/windows/x")]           // drive prefix
    [InlineData("\\\\host\\share\\x")]     // UNC prefix
    [InlineData("")]                       // empty
    [InlineData("   ")]                    // whitespace
    public void Build_InputWithTraversalOrAbsoluteDest_Throws(string dest)
    {
        var run = SeedRun();
        run.Inputs = new[] { new RunInput(Guid.NewGuid(), dest) };
        var spec = TriageAgent();

        var act = () => _builder.Build(run, spec);

        act.Should().Throw<ArgumentException>(
            "a traversal/absolute dest must fail the run start, not escape the inputs root");
    }

    [Theory]
    [InlineData("report.json", "report.json")]
    [InlineData("sub/dir/data.bin", "sub/dir/data.bin")]
    [InlineData("a//b.txt", "a/b.txt")]              // collapses empty segments
    [InlineData("dir\\file.txt", "dir/file.txt")]    // backslash normalised
    public void ValidateDestRelativePath_AcceptsAndCanonicalises(string raw, string expected)
    {
        HeadlessConfigBuilder.ValidateDestRelativePath(raw).Should().Be(expected);
    }

    // --- AX.8: policy text appended to the system prompt ---

    [Fact]
    public void ComposeInstructions_AppendsPolicyAfterObjective()
    {
        // The final system prompt must layer: role + ## Task + ## Policies,
        // in that order, so the in-container agent sees the role, then the
        // concrete task, then the governing policy.
        var result = HeadlessConfigBuilder.ComposeInstructions(
            "ROLE",
            "OBJECTIVE",
            "POLICY-TEXT");

        result.Should().Be("ROLE\n\n## Task\nOBJECTIVE\n\n## Policies\nPOLICY-TEXT");

        // Explicit ordering guard: the policy section comes after the task.
        result.IndexOf("## Policies", StringComparison.Ordinal)
            .Should().BeGreaterThan(result.IndexOf("## Task", StringComparison.Ordinal),
                "policy text must be appended after the task objective");
    }

    [Fact]
    public void ComposeInstructions_NoPolicy_LeavesPromptUnchanged()
    {
        // When no policy text is supplied the prompt is exactly role + task —
        // no trailing Policies header (backward compatible with AX/pre-AX.8).
        var result = HeadlessConfigBuilder.ComposeInstructions("ROLE", "OBJECTIVE");

        result.Should().Be("ROLE\n\n## Task\nOBJECTIVE");
        result.Should().NotContain("## Policies");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ComposeInstructions_BlankPolicy_OmitsPoliciesSection(string? policy)
    {
        var result = HeadlessConfigBuilder.ComposeInstructions("ROLE", "OBJECTIVE", policy);

        result.Should().Be("ROLE\n\n## Task\nOBJECTIVE");
    }

    [Fact]
    public void Build_WithPolicyInstructions_AppendsPolicyToSystemPrompt()
    {
        var run = SeedRun();
        run.Objective = "Add a Quick Start section to README.md.";
        run.PolicyInstructions = "You may not modify files under /infra. Never push to main.";
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Agent.Instructions.Should().Contain(spec.Instructions, "role prompt is preserved");
        config.Agent.Instructions.Should().Contain("Add a Quick Start section to README.md.",
            "the task objective is preserved");
        config.Agent.Instructions.Should().Contain("## Policies",
            "the policy section header is appended");
        config.Agent.Instructions.Should().Contain(
            "You may not modify files under /infra. Never push to main.",
            "the pre-rendered policy text is appended verbatim");

        // Ordering: policy text comes after the objective in the final prompt.
        config.Agent.Instructions.IndexOf("## Policies", StringComparison.Ordinal)
            .Should().BeGreaterThan(
                config.Agent.Instructions.IndexOf("Add a Quick Start", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithoutPolicyInstructions_PromptUnchanged()
    {
        var run = SeedRun();
        run.Objective = "Add a Quick Start section to README.md.";
        // PolicyInstructions null
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Agent.Instructions.Should().NotContain("## Policies");
    }

    // --- AX.9: allowed-tools written to permissions.allowed_tools ---

    [Fact]
    public void Build_WithAllowedTools_EmitsPermissionsAllowedToolsSnakeCase()
    {
        var run = SeedRun();
        run.AllowedTools = new[] { "write_file", "execute_command" };
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Permissions.Should().NotBeNull();
        config.Permissions!.AllowedTools.Should().Equal("write_file", "execute_command");

        // Serialises to the andy-cli AX.4 schema shape:
        // "permissions": { "allowed_tools": [...] } (snake_case).
        var json = HeadlessConfigJson.Serialize(config);
        json.Should().Contain("\"permissions\"");
        json.Should().Contain("\"allowed_tools\"");
        json.Should().Contain("\"write_file\"");
        json.Should().Contain("\"execute_command\"");

        // Round-trips back through the configurator's serializer.
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<HeadlessRunConfig>(
            json, HeadlessConfigJson.Options);
        roundTripped!.Permissions.Should().NotBeNull();
        roundTripped.Permissions!.AllowedTools.Should().Equal("write_file", "execute_command");
    }

    [Fact]
    public void Build_NoAllowedTools_OmitsPermissionsBlock()
    {
        var run = SeedRun(); // AllowedTools null
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Permissions.Should().BeNull(
            "absent allow-list means no permissions block — andy-cli stays fail-closed");

        var json = HeadlessConfigJson.Serialize(config);
        json.Should().NotContain("\"permissions\"",
            "the WhenWritingNull policy must drop the absent permissions key entirely");
    }

    [Fact]
    public void Build_EmptyAllowedTools_CollapseToNull()
    {
        var run = SeedRun();
        run.AllowedTools = Array.Empty<string>();
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Permissions.Should().BeNull(
            "empty allow-list collapses to null, matching inputs/env_vars posture");
    }

    [Fact]
    public void Build_AllowedTools_DropsBlanksAndDeduplicates()
    {
        // The schema requires uniqueItems on allowed_tools; the builder must
        // strip blank/whitespace entries and de-duplicate before emitting.
        var run = SeedRun();
        run.AllowedTools = new[] { "write_file", "  ", "write_file", "read_file", "" };
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Permissions.Should().NotBeNull();
        config.Permissions!.AllowedTools.Should().Equal("write_file", "read_file");
    }

    [Fact]
    public void Build_AllowedTools_OnlyBlanks_CollapseToNull()
    {
        var run = SeedRun();
        run.AllowedTools = new[] { "", "   " };
        var spec = TriageAgent();

        var config = _builder.Build(run, spec);

        config.Permissions.Should().BeNull(
            "an allow-list of only blanks carries no real grant — omit the block");
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
