using Andy.Containers.Abstractions.Images;
using Andy.Containers.Configuration;
using Andy.Containers.Infrastructure.Images;
using Andy.Containers.Infrastructure.Registries.Local;
using Andy.Containers.Models.ImageManagement;
using Andy.Containers.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Containers.Tests.Infrastructure.Images;

// Docker Desktop loopback gap (rivoli-ai/andy-containers). The
// ensure-pull rehost path (docker pull → tag → push) must rewrite the
// DESTINATION authority to host.docker.internal on Docker Desktop so
// the push from inside the VM reaches the host's zot — while the pull
// SOURCE (ghcr.io) is left untouched.
//
// These substitute "docker" with a StubScript recording its args, so
// no real daemon is needed. macOS / Linux only.
public class DockerCliImagePullServiceTests
{
    public static bool RunsBashStubs => !OperatingSystem.IsWindows();

    [Fact]
    public async Task EnsurePull_DockerDesktop_RewritesDestinationToHostDockerInternal()
    {
        if (!RunsBashStubs) { return; }

        using var docker = new StubScript(exitCode: 0, stderr: "");
        var service = MakeService(docker, isDockerDesktop: true);

        await service.EnsurePullAsync(new EnsurePullRequest
        {
            SourceRegistry = "ghcr.io",
            SourceRepository = "rivoli-ai/conductor-terminal-claude-code",
            SourceTag = "v1",
            DestinationRegistryId = "local-zot",
        }, CancellationToken.None);

        // pull (source unchanged), tag (source→rewritten dest), push (rewritten dest)
        docker.Invocations.Should().HaveCount(3);
        docker.Invocations[0].Should().Equal(
            ["pull", "ghcr.io/rivoli-ai/conductor-terminal-claude-code:v1"]);
        docker.Invocations[1].Should().Equal(
            ["tag",
             "ghcr.io/rivoli-ai/conductor-terminal-claude-code:v1",
             "host.docker.internal:5050/conductor-terminal-claude-code:v1"]);
        docker.Invocations[2].Should().Equal(
            ["push", "host.docker.internal:5050/conductor-terminal-claude-code:v1"]);
    }

    [Fact]
    public async Task EnsurePull_Linux_KeepsLocalhostDestination()
    {
        if (!RunsBashStubs) { return; }

        using var docker = new StubScript(exitCode: 0, stderr: "");
        var service = MakeService(docker, isDockerDesktop: false);

        await service.EnsurePullAsync(new EnsurePullRequest
        {
            SourceRegistry = "ghcr.io",
            SourceRepository = "rivoli-ai/conductor-terminal-claude-code",
            SourceTag = "v1",
            DestinationRegistryId = "local-zot",
        }, CancellationToken.None);

        docker.Invocations[2].Should().Equal(
            ["push", "localhost:5050/conductor-terminal-claude-code:v1"]);
    }

    [Fact]
    public async Task EnsurePull_PushTimeoutOnDockerDesktop_ThrowsActionableHint()
    {
        if (!RunsBashStubs) { return; }

        // docker exits non-zero on the push with the loopback-timeout
        // signature.
        using var docker = new StubScript(
            exitCode: 1,
            stderr: "Get http://host.docker.internal:5050/v2/ : Client.Timeout exceeded while awaiting headers");
        var service = MakeService(docker, isDockerDesktop: true);

        var act = async () => await service.EnsurePullAsync(new EnsurePullRequest
        {
            SourceRegistry = "ghcr.io",
            SourceRepository = "rivoli-ai/conductor-terminal-claude-code",
            SourceTag = "v1",
            DestinationRegistryId = "local-zot",
        }, CancellationToken.None);

        (await act.Should().ThrowAsync<ImagePullException>())
            .Which.Message.Should().Contain("insecure-registries");
    }

    private static DockerCliImagePullService MakeService(StubScript docker, bool isDockerDesktop)
    {
        var registryConfig = Options.Create(new RegistryConfigurationOptions
        {
            Registries =
            [
                new RegistryConfigEntry
                {
                    Id = "local-zot",
                    Kind = "zot",
                    Url = "http://localhost:5050",
                    IsPrimary = true,
                },
            ],
        });

        var pullOptions = Options.Create(new DockerCliImagePullOptions
        {
            DockerExecutablePath = docker.Path,
        });

        var pushTargetOptions = Options.Create(new PushTargetHostOptions
        {
            Mode = PushTargetHostRewriteMode.Auto,
            IsDockerDesktopOverride = isDockerDesktop,
        });

        return new DockerCliImagePullService(
            registryAdapters: [new NeverPresentAdapter()],
            registryConfig: registryConfig,
            logger: NullLogger<DockerCliImagePullService>.Instance,
            options: pullOptions,
            pushTargetOptions: pushTargetOptions);
    }

    /// <summary>
    /// Registry adapter whose idempotency probe always reports nothing
    /// present (so the pull always runs), then reports a reference once
    /// the push has happened (so the post-push lookup succeeds).
    /// </summary>
    private sealed class NeverPresentAdapter : IRegistryAdapter
    {
        private int _listCalls;

        public string RegistryId => "local-zot";

        public Task<IReadOnlyList<RegistryReference>> ListReferencesAsync(string repoPath, CancellationToken ct)
        {
            // First call = pre-push idempotency probe → empty.
            // Subsequent = post-push lookup → one reference.
            _listCalls++;
            IReadOnlyList<RegistryReference> result = _listCalls <= 1
                ? []
                : [new RegistryReference(
                    Id: Guid.NewGuid(), RegistryId: RegistryId, RepoPath: repoPath, Tag: "v1",
                    Digest: "sha256:pushed", PushedAt: DateTimeOffset.UtcNow, PushedBy: "test")];
            return Task.FromResult(result);
        }

        public Task<RegistryReference> PushAsync(BuildArtifact artifact, string repoPath, string tag, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string repoPath, string digest, CancellationToken ct)
            => Task.FromResult(false);

        public Task DeleteAsync(RegistryReference reference, CancellationToken ct)
            => Task.CompletedTask;
    }
}
