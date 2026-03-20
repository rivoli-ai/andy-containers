using Andy.Containers.Api.Telemetry;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Telemetry;

public class MetersTests
{
    [Fact]
    public void All_counter_names_follow_naming_convention()
    {
        Meters.ContainersCreated.Name.Should().StartWith("andy.containers.");
        Meters.ContainersDeleted.Name.Should().StartWith("andy.containers.");
        Meters.GitClonesCompleted.Name.Should().StartWith("andy.containers.");
        Meters.GitClonesFailed.Name.Should().StartWith("andy.containers.");
        Meters.ProvisioningErrors.Name.Should().StartWith("andy.containers.");
    }

    [Fact]
    public void All_histogram_names_follow_naming_convention()
    {
        Meters.ProvisioningDuration.Name.Should().StartWith("andy.containers.");
        Meters.GitCloneDuration.Name.Should().StartWith("andy.containers.");
        Meters.ImageDiffDuration.Name.Should().StartWith("andy.containers.");
    }

    [Fact]
    public void All_array_is_populated()
    {
        Meters.All.Should().NotBeEmpty();
        Meters.All.Should().Contain("Andy.Containers");
    }
}
