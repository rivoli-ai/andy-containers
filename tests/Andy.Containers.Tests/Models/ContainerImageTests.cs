using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class ContainerImageTests
{
    [Fact]
    public void NewImage_ShouldHaveDefaultBuildStatus_Pending()
    {
        var image = CreateImage();

        image.BuildStatus.Should().Be(ImageBuildStatus.Pending);
    }

    [Fact]
    public void NewImage_ShouldHaveCreatedAtSet()
    {
        var before = DateTime.UtcNow;
        var image = CreateImage();
        var after = DateTime.UtcNow;

        image.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void NewImage_ShouldNotBeBuiltOffline()
    {
        var image = CreateImage();

        image.BuiltOffline.Should().BeFalse();
    }

    [Fact]
    public void NewImage_NullableProperties_ShouldBeNull()
    {
        var image = CreateImage();

        image.BuildLog.Should().BeNull();
        image.BuildStartedAt.Should().BeNull();
        image.BuildCompletedAt.Should().BeNull();
        image.ImageSizeBytes.Should().BeNull();
        image.PreviousImageId.Should().BeNull();
        image.PreviousImage.Should().BeNull();
        image.Changelog.Should().BeNull();
        image.Metadata.Should().BeNull();
        image.Template.Should().BeNull();
    }

    [Theory]
    [InlineData(ImageBuildStatus.Pending)]
    [InlineData(ImageBuildStatus.Building)]
    [InlineData(ImageBuildStatus.Succeeded)]
    [InlineData(ImageBuildStatus.Failed)]
    public void ImageBuildStatus_ShouldAcceptAllValues(ImageBuildStatus status)
    {
        var image = CreateImage();
        image.BuildStatus = status;

        image.BuildStatus.Should().Be(status);
    }

    [Fact]
    public void Image_ShouldStoreRequiredFields()
    {
        var image = new ContainerImage
        {
            ContentHash = "sha256:abc123",
            Tag = "full-stack:1.2.0-42",
            ImageReference = "registry.example.com/andy/full-stack:1.2.0-42@sha256:abc123",
            BaseImageDigest = "sha256:baseabc",
            DependencyManifest = "{\"dependencies\":[]}",
            DependencyLock = "{\"locked\":[]}",
            BuildNumber = 42
        };

        image.ContentHash.Should().Be("sha256:abc123");
        image.Tag.Should().Be("full-stack:1.2.0-42");
        image.ImageReference.Should().Contain("registry.example.com");
        image.BaseImageDigest.Should().Be("sha256:baseabc");
        image.BuildNumber.Should().Be(42);
    }

    [Fact]
    public void Image_PreviousImageChain_ShouldBeSettable()
    {
        var previousId = Guid.NewGuid();
        var image = CreateImage();
        image.PreviousImageId = previousId;

        image.PreviousImageId.Should().Be(previousId);
    }

    private static ContainerImage CreateImage() => new()
    {
        ContentHash = "sha256:test",
        Tag = "test:1.0.0-1",
        ImageReference = "registry/test:1.0.0-1",
        BaseImageDigest = "sha256:base",
        DependencyManifest = "{}",
        DependencyLock = "{}"
    };
}
