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
        errors.Should().ContainSingle().Which.Should().Contain("path traversal");
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
