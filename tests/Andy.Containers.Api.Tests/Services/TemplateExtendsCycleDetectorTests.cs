using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// IM4 (rivoli-ai/andy-containers#253). Cycle detection for the
// extends chain. Detection happens at register-time so a cycle is
// caught before any build is queued.
public class TemplateExtendsCycleDetectorTests
{
    private static Func<string, ExtendsLookup> Resolver(Dictionary<string, string?> graph)
        => code => graph.TryGetValue(code, out var v)
            ? string.IsNullOrEmpty(v) ? ExtendsLookup.End : ExtendsLookup.Extends(v)
            : ExtendsLookup.Missing;

    [Fact]
    public void Validate_AcyclicChain_ReturnsOk()
    {
        // child → parent → root → (none)
        var graph = new Dictionary<string, string?>
        {
            ["child"] = "parent",
            ["parent"] = "root",
            ["root"] = null,
        };

        var result = TemplateExtendsCycleDetector.Validate("child", Resolver(graph));

        result.IsValid.Should().BeTrue();
        result.Path.Should().Equal(["child", "parent", "root"]);
    }

    [Fact]
    public void Validate_TemplateWithoutExtends_ReturnsOkWithSingleNodePath()
    {
        var graph = new Dictionary<string, string?>
        {
            ["solo"] = null,
        };

        var result = TemplateExtendsCycleDetector.Validate("solo", Resolver(graph));

        result.IsValid.Should().BeTrue();
        result.Path.Should().Equal(["solo"]);
    }

    [Fact]
    public void Validate_SelfLoop_DetectsCycle()
    {
        // a → a (direct self-loop)
        var graph = new Dictionary<string, string?>
        {
            ["a"] = "a",
        };

        var result = TemplateExtendsCycleDetector.Validate("a", Resolver(graph));

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(ExtendsResolutionStatus.Cycle);
        result.Path.Should().Equal(["a", "a"]);
        result.Describe().Should().Contain("cycle");
    }

    [Fact]
    public void Validate_TwoCycle_DetectsCycle()
    {
        // a → b → a
        var graph = new Dictionary<string, string?>
        {
            ["a"] = "b",
            ["b"] = "a",
        };

        var result = TemplateExtendsCycleDetector.Validate("a", Resolver(graph));

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(ExtendsResolutionStatus.Cycle);
        result.Path.Should().Equal(["a", "b", "a"]);
    }

    [Fact]
    public void Validate_LongerCycle_DetectsCycle()
    {
        // a → b → c → d → b
        var graph = new Dictionary<string, string?>
        {
            ["a"] = "b",
            ["b"] = "c",
            ["c"] = "d",
            ["d"] = "b",
        };

        var result = TemplateExtendsCycleDetector.Validate("a", Resolver(graph));

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(ExtendsResolutionStatus.Cycle);
        result.Path.Should().Equal(["a", "b", "c", "d", "b"]);
    }

    [Fact]
    public void Validate_MissingParent_FlagsTheUnresolvedCode()
    {
        // child references parent, but parent isn't in the graph.
        var graph = new Dictionary<string, string?>
        {
            ["child"] = "parent",
        };

        var result = TemplateExtendsCycleDetector.Validate("child", Resolver(graph));

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(ExtendsResolutionStatus.MissingParent);
        result.MissingParentOf.Should().Be("parent");
        result.Describe().Should().Contain("'parent'");
    }

    [Fact]
    public void Validate_ChainExceedingMaxDepth_IsTooDeep()
    {
        // Build a perfectly acyclic chain whose length exceeds
        // MaxChainDepth (16). The detector caps depth and surfaces
        // it explicitly so an operator looks at the path.
        var graph = new Dictionary<string, string?>();
        for (var i = 0; i < TemplateExtendsCycleDetector.MaxChainDepth + 5; i++)
        {
            graph[$"t{i}"] = $"t{i + 1}";
        }
        graph[$"t{TemplateExtendsCycleDetector.MaxChainDepth + 5}"] = null;

        var result = TemplateExtendsCycleDetector.Validate("t0", Resolver(graph));

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(ExtendsResolutionStatus.TooDeep);
    }

    [Fact]
    public void Validate_EmptyExtendsString_TreatedAsChainEnd()
    {
        // Some YAML parsers serialise `extends: ""` rather than
        // null; both should be treated as "no parent."
        var graph = new Dictionary<string, string?>
        {
            ["a"] = "",
        };

        var result = TemplateExtendsCycleDetector.Validate("a", Resolver(graph));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_CaseInsensitiveCycleDetection()
    {
        // Template codes are lowercase by convention but the
        // detector should not be tripped by case differences in
        // a buggy lookup that uppercased keys.
        var graph = new Dictionary<string, string?>
        {
            ["a"] = "B",   // points to mixed-case
            ["b"] = "a",
            ["B"] = "a",
        };

        var result = TemplateExtendsCycleDetector.Validate("a", Resolver(graph));

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(ExtendsResolutionStatus.Cycle);
    }
}
