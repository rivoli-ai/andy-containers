using Andy.Containers.Abstractions.Images;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Abstractions.Images;

// SM.2.7 (rivoli-ai/conductor#2009).
// Unit tests for the BuildFailureReason taxonomy and transient/permanent
// classification. Every branch of BuildFailureReasonExtensions.IsTransient
// is covered, plus the new BuildFailureEvent and BuildCachedEvent record
// shapes are exercised as compile-time contracts.
public class BuildFailureTaxonomyTests
{
    // --- IsTransient classification ---

    [Theory]
    [InlineData(BuildFailureReason.RegistryUnreachable, true,  "transient — retry may succeed")]
    [InlineData(BuildFailureReason.EngineUnavailable,   true,  "transient — docker daemon may recover")]
    [InlineData(BuildFailureReason.PullInterrupted,     true,  "transient — network drop, retry makes sense")]
    [InlineData(BuildFailureReason.ManifestUnknown,     false, "permanent — tag/repo does not exist")]
    [InlineData(BuildFailureReason.DigestMismatch,      false, "permanent — wrong bytes in registry")]
    [InlineData(BuildFailureReason.ImagePullFailed,     false, "permanent — policy / verification failure")]
    [InlineData(BuildFailureReason.Unknown,             false, "permanent — unknown = surface for operator review")]
    public void IsTransient_ReturnsExpectedClassification(
        BuildFailureReason reason,
        bool expectedTransient,
        string because)
    {
        reason.IsTransient().Should().Be(expectedTransient, because);
    }

    // --- BuildFailureEvent record shape ---

    [Fact]
    public void BuildFailureEvent_ExposesRequiredProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new BuildFailureEvent
        {
            Timestamp = now,
            Reason    = BuildFailureReason.DigestMismatch,
            Transient = false,
            Detail    = "expected sha256:abc got sha256:def",
        };

        evt.Timestamp.Should().Be(now);
        evt.Reason.Should().Be(BuildFailureReason.DigestMismatch);
        evt.Transient.Should().BeFalse();
        evt.Detail.Should().Be("expected sha256:abc got sha256:def");
    }

    [Fact]
    public void BuildFailureEvent_TransientProperty_MirrorsTaxonomy()
    {
        // The Transient field is set by the emitter using IsTransient().
        // Verify that setting it consistently with the taxonomy is coherent.
        foreach (BuildFailureReason reason in Enum.GetValues<BuildFailureReason>())
        {
            var evt = new BuildFailureEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Reason    = reason,
                Transient = reason.IsTransient(),
                Detail    = null,
            };

            evt.Transient.Should().Be(reason.IsTransient(),
                because: $"Transient on the event should mirror IsTransient() for {reason}");
        }
    }

    // --- BuildCachedEvent record shape ---

    [Fact]
    public void BuildCachedEvent_ExposesDigest()
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new BuildCachedEvent
        {
            Timestamp = now,
            Digest    = "sha256:abc123",
        };

        evt.Timestamp.Should().Be(now);
        evt.Digest.Should().Be("sha256:abc123");
    }

    [Fact]
    public void BuildCachedEvent_AllowsNullDigest()
    {
        // Digest may be null when the cache hit was detected before the
        // digest was resolved.
        var evt = new BuildCachedEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Digest    = null,
        };

        evt.Digest.Should().BeNull();
    }

    // --- BuildProgressEvent polymorphism ---

    [Fact]
    public void BuildFailureEvent_IsBuildProgressEvent()
    {
        BuildProgressEvent evt = new BuildFailureEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Reason    = BuildFailureReason.RegistryUnreachable,
            Transient = true,
        };

        evt.Should().BeOfType<BuildFailureEvent>();
    }

    [Fact]
    public void BuildCachedEvent_IsBuildProgressEvent()
    {
        BuildProgressEvent evt = new BuildCachedEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
        };

        evt.Should().BeOfType<BuildCachedEvent>();
    }
}
