using Andy.Containers.Api.Telemetry;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Telemetry;

public class ActivitySourcesTests
{
    [Fact]
    public void All_ActivitySource_names_are_non_null_and_start_with_prefix()
    {
        ActivitySources.Provisioning.Name.Should().StartWith("Andy.Containers.");
        ActivitySources.Introspection.Name.Should().StartWith("Andy.Containers.");
        ActivitySources.Git.Name.Should().StartWith("Andy.Containers.");
        ActivitySources.Infrastructure.Name.Should().StartWith("Andy.Containers.");
    }

    [Fact]
    public void All_array_contains_all_source_names()
    {
        ActivitySources.All.Should().HaveCount(4);
        ActivitySources.All.Should().Contain(ActivitySources.Provisioning.Name);
        ActivitySources.All.Should().Contain(ActivitySources.Introspection.Name);
        ActivitySources.All.Should().Contain(ActivitySources.Git.Name);
        ActivitySources.All.Should().Contain(ActivitySources.Infrastructure.Name);
    }
}
