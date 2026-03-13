using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class GitUrlValidatorTests
{
    [Theory]
    [InlineData("https://github.com/org/repo.git", true)]
    [InlineData("https://gitlab.com/org/repo", true)]
    [InlineData("http://github.com/org/repo.git", true)]
    [InlineData("git@github.com:org/repo.git", true)]
    [InlineData("git@gitlab.com:org/subgroup/repo.git", true)]
    [InlineData("file:///local/repo", false)]
    [InlineData("ftp://server/repo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-a-url", false)]
    public void IsValidGitUrl_ReturnsExpected(string? url, bool expected)
    {
        GitUrlValidator.IsValidGitUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://user:password@github.com/org/repo.git", true)]
    [InlineData("https://token:x-oauth-basic@github.com/org/repo.git", true)]
    [InlineData("https://github.com/org/repo.git", false)]
    [InlineData("git@github.com:org/repo.git", false)]
    public void HasEmbeddedCredentials_ReturnsExpected(string url, bool expected)
    {
        GitUrlValidator.HasEmbeddedCredentials(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/my-branch", true)]
    [InlineData("release/v1.0.0", true)]
    [InlineData("abc123def456abc123def456abc123def456abcdef", true)] // 40-char SHA
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidBranchName_ReturnsExpected(string? branch, bool expected)
    {
        GitUrlValidator.IsValidBranchName(branch).Should().Be(expected);
    }

    [Theory]
    [InlineData("my-repo", true)]
    [InlineData("src/my-repo", true)]
    [InlineData("/absolute/path", false)]
    [InlineData("../escape/path", false)]
    [InlineData("path/../escape", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidTargetPath_ReturnsExpected(string? path, bool expected)
    {
        GitUrlValidator.IsValidTargetPath(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", "github.com")]
    [InlineData("https://gitlab.com/org/repo", "gitlab.com")]
    [InlineData("git@github.com:org/repo.git", "github.com")]
    [InlineData("git@bitbucket.org:team/repo.git", "bitbucket.org")]
    [InlineData("not-a-url", null)]
    public void ExtractHost_ReturnsExpected(string url, string? expected)
    {
        GitUrlValidator.ExtractHost(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/org/my-repo.git", "my-repo")]
    [InlineData("https://github.com/org/my-repo", "my-repo")]
    [InlineData("git@github.com:org/cool-project.git", "cool-project")]
    public void DeriveTargetPath_ReturnsExpected(string url, string expected)
    {
        GitUrlValidator.DeriveTargetPath(url).Should().Be(expected);
    }
}
