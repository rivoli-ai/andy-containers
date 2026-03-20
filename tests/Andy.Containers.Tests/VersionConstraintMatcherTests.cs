using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

public class VersionConstraintMatcherTests
{
    // --- "latest" ---

    [Theory]
    [InlineData("8.0.404")]
    [InlineData("3.12.8")]
    [InlineData("1.0.0")]
    public void Latest_ShouldMatchAnyVersion(string version)
    {
        VersionConstraintMatcher.Matches("latest", version).Should().BeTrue();
    }

    [Fact]
    public void Latest_CaseInsensitive_ShouldMatch()
    {
        VersionConstraintMatcher.Matches("Latest", "1.0.0").Should().BeTrue();
        VersionConstraintMatcher.Matches("LATEST", "1.0.0").Should().BeTrue();
    }

    // --- Exact match ---

    [Fact]
    public void ExactMatch_SameVersion_ShouldMatch()
    {
        VersionConstraintMatcher.Matches("8.0.404", "8.0.404").Should().BeTrue();
    }

    [Fact]
    public void ExactMatch_DifferentVersion_ShouldNotMatch()
    {
        VersionConstraintMatcher.Matches("8.0.404", "8.0.405").Should().BeFalse();
    }

    // --- Wildcard ---

    [Theory]
    [InlineData("8.0.*", "8.0.404", true)]
    [InlineData("8.0.*", "8.0.0", true)]
    [InlineData("8.0.*", "8.1.0", false)]
    [InlineData("8.*", "8.1.0", true)]
    [InlineData("8.*", "8.0.404", true)]
    [InlineData("8.*", "9.0.0", false)]
    public void Wildcard_ShouldMatchCorrectly(string constraint, string version, bool expected)
    {
        VersionConstraintMatcher.Matches(constraint, version).Should().Be(expected);
    }

    [Theory]
    [InlineData("8.x", "8.1.0", true)]
    [InlineData("8.0.x", "8.0.404", true)]
    [InlineData("8.0.x", "8.1.0", false)]
    public void WildcardX_ShouldMatchCorrectly(string constraint, string version, bool expected)
    {
        VersionConstraintMatcher.Matches(constraint, version).Should().Be(expected);
    }

    // --- Range operators ---

    [Theory]
    [InlineData(">=3.12,<4.0", "3.12.8", true)]
    [InlineData(">=3.12,<4.0", "3.12.0", true)]
    [InlineData(">=3.12,<4.0", "3.11.9", false)]
    [InlineData(">=3.12,<4.0", "4.0.0", false)]
    [InlineData(">=3.12,<4.0", "3.99.99", true)]
    public void Range_ShouldMatchCorrectly(string constraint, string version, bool expected)
    {
        VersionConstraintMatcher.Matches(constraint, version).Should().Be(expected);
    }

    [Theory]
    [InlineData(">=8.0.0", "8.0.404", true)]
    [InlineData(">=8.0.0", "8.0.0", true)]
    [InlineData(">=8.0.0", "7.99.99", false)]
    public void GreaterThanOrEqual_ShouldMatchCorrectly(string constraint, string version, bool expected)
    {
        VersionConstraintMatcher.Matches(constraint, version).Should().Be(expected);
    }

    [Theory]
    [InlineData("<9.0", "8.5.0", true)]
    [InlineData("<9.0", "9.0.0", false)]
    [InlineData("<9.0", "8.99.99", true)]
    public void LessThan_ShouldMatchCorrectly(string constraint, string version, bool expected)
    {
        VersionConstraintMatcher.Matches(constraint, version).Should().Be(expected);
    }

    [Theory]
    [InlineData(">8.0.0", "8.0.1", true)]
    [InlineData(">8.0.0", "8.0.0", false)]
    [InlineData("<=8.0.0", "8.0.0", true)]
    [InlineData("<=8.0.0", "8.0.1", false)]
    public void StrictOperators_ShouldMatchCorrectly(string constraint, string version, bool expected)
    {
        VersionConstraintMatcher.Matches(constraint, version).Should().Be(expected);
    }

    // --- Null/empty constraint ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyConstraint_ShouldAlwaysMatch(string? constraint)
    {
        VersionConstraintMatcher.Matches(constraint, "8.0.404").Should().BeTrue();
    }

    // --- ChangeSeverity ---

    [Fact]
    public void ClassifyChange_MajorVersionDifference_ShouldReturnMajor()
    {
        VersionConstraintMatcher.ClassifyChange("8.0.404", "9.0.0").Should().Be(ChangeSeverity.Major);
    }

    [Fact]
    public void ClassifyChange_MinorVersionDifference_ShouldReturnMinor()
    {
        VersionConstraintMatcher.ClassifyChange("8.0.400", "8.1.0").Should().Be(ChangeSeverity.Minor);
    }

    [Fact]
    public void ClassifyChange_PatchVersionDifference_ShouldReturnPatch()
    {
        VersionConstraintMatcher.ClassifyChange("8.0.400", "8.0.404").Should().Be(ChangeSeverity.Patch);
    }

    [Fact]
    public void ClassifyChange_IdenticalVersions_ShouldReturnBuild()
    {
        VersionConstraintMatcher.ClassifyChange("8.0.400", "8.0.400").Should().Be(ChangeSeverity.Build);
    }

    [Fact]
    public void ClassifyChange_NonSemverWithSuffix_ShouldHandleGracefully()
    {
        // "9.6p1" → major=9, minor=6, patch=0 (p1 stripped)
        VersionConstraintMatcher.ClassifyChange("9.6p1", "9.7p1").Should().Be(ChangeSeverity.Minor);
    }

    [Fact]
    public void ClassifyChange_Downgrade_ShouldStillClassifyCorrectly()
    {
        VersionConstraintMatcher.ClassifyChange("9.0.0", "8.0.0").Should().Be(ChangeSeverity.Major);
    }
}
