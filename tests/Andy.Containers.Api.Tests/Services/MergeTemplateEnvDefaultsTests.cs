using System.Collections.Generic;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// Guards <see cref="ContainerOrchestrationService.MergeTemplateEnvDefaults"/>:
/// a template's JSON env defaults are merged into the container env at the
/// LOWEST precedence (codeAssistant config + explicit request env both win),
/// so a container launched from the catalog with only a templateCode (the
/// Sessions UI path) comes up configured — without ever throwing out of the
/// create path on bad input.
/// </summary>
public class MergeTemplateEnvDefaultsTests
{
    private static Dictionary<string, string>? Merge(
        Dictionary<string, string>? env, string? json)
        => ContainerOrchestrationService.MergeTemplateEnvDefaults(
            env, json, "andy-cli-agent", NullLogger.Instance);

    [Fact]
    public void Adds_template_defaults_when_env_is_null()
    {
        var json = """{"OPENROUTER_MODEL":"xiaomi/mimo-v2.5","OPENROUTER_API_BASE":"https://openrouter.ai/api/v1"}""";

        var result = Merge(null, json);

        result.Should().NotBeNull();
        result!["OPENROUTER_MODEL"].Should().Be("xiaomi/mimo-v2.5");
        result["OPENROUTER_API_BASE"].Should().Be("https://openrouter.ai/api/v1");
    }

    [Fact]
    public void Does_not_override_existing_keys()
    {
        // OPENROUTER_API_KEY already set by an explicit request env / codeAssistant
        // must NOT be clobbered by the template default.
        var env = new Dictionary<string, string>
        {
            ["OPENROUTER_API_KEY"] = "sk-from-request",
        };
        var json = """{"OPENROUTER_API_KEY":"sk-template-default","OPENROUTER_MODEL":"xiaomi/mimo-v2.5"}""";

        var result = Merge(env, json);

        result!["OPENROUTER_API_KEY"].Should().Be("sk-from-request", "explicit env wins over template default");
        result["OPENROUTER_MODEL"].Should().Be("xiaomi/mimo-v2.5", "non-conflicting template keys are still added");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Returns_input_unchanged_for_null_or_empty_json(string? json)
    {
        var env = new Dictionary<string, string> { ["A"] = "1" };

        var result = Merge(env, json);

        result.Should().BeSameAs(env);
    }

    [Fact]
    public void Returns_null_unchanged_for_empty_json_when_env_null()
    {
        Merge(null, null).Should().BeNull();
    }

    [Fact]
    public void Invalid_json_is_ignored_and_does_not_throw()
    {
        var env = new Dictionary<string, string> { ["A"] = "1" };

        var act = () => Merge(env, "{ not valid json ");

        act.Should().NotThrow();
        var result = Merge(env, "{ not valid json ");
        result.Should().BeSameAs(env);
        result!["A"].Should().Be("1");
    }
}
