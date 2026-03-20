using Andy.Containers.Models;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Models;

public class ContainerGitRepositoryTests
{
    [Fact]
    public void NewRepository_ShouldHaveDefaultValues()
    {
        var repo = new ContainerGitRepository { Url = "https://github.com/test/repo" };

        repo.CloneStatus.Should().Be(GitCloneStatus.Pending);
        repo.TargetPath.Should().Be("/workspace");
        repo.Submodules.Should().BeFalse();
        repo.IsFromTemplate.Should().BeFalse();
        repo.CloneError.Should().BeNull();
        repo.CloneStartedAt.Should().BeNull();
        repo.CloneCompletedAt.Should().BeNull();
        repo.Branch.Should().BeNull();
        repo.CredentialRef.Should().BeNull();
        repo.CloneDepth.Should().BeNull();
    }

    [Fact]
    public void NewRepository_ShouldHaveCreatedAtSet()
    {
        var before = DateTime.UtcNow;
        var repo = new ContainerGitRepository { Url = "https://github.com/test/repo" };
        var after = DateTime.UtcNow;

        repo.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Theory]
    [InlineData(GitCloneStatus.Pending)]
    [InlineData(GitCloneStatus.Cloning)]
    [InlineData(GitCloneStatus.Cloned)]
    [InlineData(GitCloneStatus.Failed)]
    [InlineData(GitCloneStatus.Pulling)]
    public void CloneStatus_ShouldAcceptAllValues(GitCloneStatus status)
    {
        var repo = new ContainerGitRepository { Url = "https://github.com/test/repo" };
        repo.CloneStatus = status;
        repo.CloneStatus.Should().Be(status);
    }

    [Fact]
    public void Repository_ShouldStoreAllProperties()
    {
        var containerId = Guid.NewGuid();
        var repo = new ContainerGitRepository
        {
            ContainerId = containerId,
            Url = "https://github.com/test/repo",
            Branch = "develop",
            TargetPath = "/home/user/project",
            CredentialRef = "my-github-pat",
            CloneDepth = 1,
            Submodules = true,
            IsFromTemplate = true
        };

        repo.ContainerId.Should().Be(containerId);
        repo.Url.Should().Be("https://github.com/test/repo");
        repo.Branch.Should().Be("develop");
        repo.TargetPath.Should().Be("/home/user/project");
        repo.CredentialRef.Should().Be("my-github-pat");
        repo.CloneDepth.Should().Be(1);
        repo.Submodules.Should().BeTrue();
        repo.IsFromTemplate.Should().BeTrue();
    }
}
