using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// Guards the reliable gh-install fix (2026-06-29): both the PR-author agent
/// (<c>gh pr create</c>) and the PR-deliverable verifier (<c>gh pr view</c>)
/// shell out to <c>gh</c> inside the container, but the base image is bare
/// <c>ubuntu:24.04</c>. A missing <c>gh</c> surfaced as the misleading
/// <c>[PR-VERIFY-002] "no open PR"</c>.
/// </summary>
public class ContainerToolProvisionerTests
{
    private static readonly Guid Container = Guid.NewGuid();

    private static (ContainerToolProvisioner sut, Mock<IContainerService> containers) Build(int exitCode)
    {
        var containers = new Mock<IContainerService>();
        containers.Setup(c => c.ExecAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecResult { ExitCode = exitCode, StdErr = exitCode == 0 ? null : "boom" });
        var sut = new ContainerToolProvisioner(containers.Object, NullLogger<ContainerToolProvisioner>.Instance);
        return (sut, containers);
    }

    [Fact]
    public async Task Execs_an_idempotent_reliable_install_and_reports_success()
    {
        var (sut, containers) = Build(exitCode: 0);
        string? script = null;
        containers.Setup(c => c.ExecAsync(Container, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, TimeSpan, CancellationToken>((_, s, _, _) => script = s)
            .ReturnsAsync(new ExecResult { ExitCode = 0 });

        var ok = await sut.EnsureGitHubCliAsync(Container);

        ok.Should().BeTrue();
        script.Should().NotBeNull();
        // Idempotent guard so re-running on a container that already has gh is a no-op.
        script!.Should().Contain("command -v gh");
        // The reliable apt-keyring method (not the brittle api.github.com-only path).
        script!.Should().Contain("cli.github.com/packages");
        script!.Should().Contain("githubcli-archive-keyring.gpg");
        // The exit code is driven by a real final check, not swallowed.
        script!.TrimEnd().Should().EndWith("command -v gh >/dev/null 2>&1");
    }

    [Fact]
    public async Task Reports_failure_when_install_cannot_complete()
    {
        var (sut, _) = Build(exitCode: 1);

        var ok = await sut.EnsureGitHubCliAsync(Container);

        // Best-effort: returns false (logged) rather than throwing, so a
        // no-network container never blocks the run from starting.
        ok.Should().BeFalse();
    }

    [Fact]
    public void Install_script_is_shared_with_provisioning_PostCreate()
    {
        // The const is referenced by DataSeeder.PostCreateScript; this pins
        // that it stays a single source of truth (compile-time concatenation
        // would break if the symbol were renamed/removed).
        ContainerToolProvisioner.GitHubCliInstallScript.Should().Contain("gh");
    }
}
