using System.Security.Claims;
using Andy.Containers.Abstractions.Images;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Tests.Abstractions.Images;

// IM2 (rivoli-ai/andy-containers#251). The abstractions are intended
// to be implementable by stub/no-op classes for tests and by per-vendor
// adapters in IM6+. These tests exercise the contracts via stubs so a
// later refactor that breaks an interface signature surfaces here as
// a compile error rather than at adapter-implementation time.
public class AbstractionContractTests
{
    [Fact]
    public async Task IRegistryAdapter_PushAsync_ReturnsReferenceWithMatchingDigest()
    {
        var adapter = new StubRegistryAdapter("local-zot");
        var artifact = MakeArtifact("sha256:abc");

        var reference = await adapter.PushAsync(artifact, "foo/bar", "v1", CancellationToken.None);

        reference.RegistryId.Should().Be("local-zot");
        reference.RepoPath.Should().Be("foo/bar");
        reference.Tag.Should().Be("v1");
        reference.Digest.Should().Be(artifact.Digest);
    }

    [Fact]
    public async Task IBuildBackend_BuildAsync_SurfacesProgressEvents()
    {
        var backend = new StubBuildBackend();
        var spec = new TemplateSpec("conductor-terminal", "1.0.0", "sha256:def", "{}");
        var context = new StubBuildContext("/tmp/build-xyz");
        var events = new List<BuildProgressEvent>();
        var progress = new Progress<BuildProgressEvent>(events.Add);

        var artifact = await backend.BuildAsync(spec, context, progress, CancellationToken.None);

        artifact.SpecHash.Should().Be("sha256:def");

        // Allow the Progress<T> SynchronizationContext callback to flush.
        await Task.Delay(50);

        events.Should().NotBeEmpty();
        events.OfType<BuildStepStartedEvent>().Should().HaveCount(1);
        events.OfType<BuildCompletedEvent>().Should().ContainSingle()
            .Which.Outcome.Should().Be(BuildOutcome.Succeeded);
    }

    [Fact]
    public async Task IIdentityBridge_GetCredentialAsync_ReturnsCredentialForRegistry()
    {
        var bridge = new StubIdentityBridge();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-123"),
        ]));

        var credential = await bridge.GetCredentialAsync("local-zot", principal, CancellationToken.None);

        credential.RegistryId.Should().Be("local-zot");
        credential.Scheme.Should().Be("Bearer");
        credential.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task IPullCredentialBroker_MintAsync_RespectsTtl()
    {
        var broker = new StubPullCredentialBroker();
        var ttl = TimeSpan.FromMinutes(15);

        var credential = await broker.MintAsync("local-zot", "foo/bar", ttl, CancellationToken.None);

        credential.RegistryId.Should().Be("local-zot");
        credential.RepoPath.Should().Be("foo/bar");
        credential.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow + ttl, TimeSpan.FromSeconds(5));
        credential.DockerConfigJson.Should().Contain("auths");
    }

    [Fact]
    public void ImageBuildFailedException_CarriesCapturedLogs()
    {
        var exception = new ImageBuildFailedException(
            backendId: "local-docker",
            capturedLogs: "Step 5/12 : RUN apt-get install -y bogus\n  E: Unable to locate package bogus",
            specHash: "sha256:bad",
            failingStepName: "packages-install");

        exception.BackendId.Should().Be("local-docker");
        exception.SpecHash.Should().Be("sha256:bad");
        exception.FailingStepName.Should().Be("packages-install");
        exception.CapturedLogs.Should().Contain("Unable to locate package");
        exception.Message.Should().Contain("packages-install");
    }

    private static BuildArtifact MakeArtifact(string digest)
        => new(
            Digest: digest,
            MediaType: "application/vnd.oci.image.manifest.v1+json",
            SizeBytes: 12_345_678,
            SpecHash: "sha256:def",
            LocalReference: "andy-containers-build-tmp:abc");

    private sealed class StubRegistryAdapter : IRegistryAdapter
    {
        public StubRegistryAdapter(string id) { RegistryId = id; }
        public string RegistryId { get; }

        public Task<RegistryReference> PushAsync(BuildArtifact artifact, string repoPath, string tag, CancellationToken ct)
            => Task.FromResult(new RegistryReference(
                Id: Guid.NewGuid(),
                RegistryId: RegistryId,
                RepoPath: repoPath,
                Tag: tag,
                Digest: artifact.Digest,
                PushedAt: DateTimeOffset.UtcNow,
                PushedBy: "stub"));

        public Task<bool> ExistsAsync(string repoPath, string digest, CancellationToken ct)
            => Task.FromResult(false);

        public Task<IReadOnlyList<RegistryReference>> ListReferencesAsync(string repoPath, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RegistryReference>>(Array.Empty<RegistryReference>());

        public Task DeleteAsync(RegistryReference reference, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StubBuildBackend : IBuildBackend
    {
        public string BackendId => "stub-backend";

        public BuildBackendCapabilities Capabilities => new(
            SupportsMultiArch: false,
            SupportedArchitectures: ["amd64"],
            SupportsCacheImport: false,
            SupportsRemoteContext: false,
            SupportsSecrets: false);

        public Task<BuildArtifact> BuildAsync(
            TemplateSpec spec,
            IBuildContext context,
            IProgress<BuildProgressEvent> progress,
            CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            progress.Report(new BuildStepStartedEvent
            {
                Timestamp = now,
                StepName = "stub-step",
                StepIndex = 1,
                TotalSteps = 1,
            });
            progress.Report(new BuildCompletedEvent
            {
                Timestamp = now,
                Outcome = BuildOutcome.Succeeded,
                Digest = "sha256:stub",
            });
            return Task.FromResult(new BuildArtifact(
                Digest: "sha256:stub",
                MediaType: "application/vnd.oci.image.manifest.v1+json",
                SizeBytes: 0,
                SpecHash: spec.SpecHash,
                LocalReference: "stub:" + spec.SpecHash));
        }
    }

    private sealed class StubBuildContext : IBuildContext
    {
        public StubBuildContext(string path) { ContextDirectoryPath = path; }
        public string ContextDirectoryPath { get; }
        public IReadOnlyList<UploadedFile> Files => Array.Empty<UploadedFile>();
    }

    private sealed class StubIdentityBridge : IIdentityBridge
    {
        public Task<RegistryCredential> GetCredentialAsync(
            string registryId,
            ClaimsPrincipal principal,
            CancellationToken ct)
            => Task.FromResult(new RegistryCredential(
                RegistryId: registryId,
                Scheme: "Bearer",
                Token: "stub-token-for-" + (principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anon"),
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)));
    }

    private sealed class StubPullCredentialBroker : IPullCredentialBroker
    {
        public Task<WorkspacePullCredential> MintAsync(
            string registryId,
            string repoPath,
            TimeSpan ttl,
            CancellationToken ct)
            => Task.FromResult(new WorkspacePullCredential(
                RegistryId: registryId,
                RepoPath: repoPath,
                DockerConfigJson: "{\"auths\":{\"" + registryId + "\":{\"auth\":\"stub\"}}}",
                ExpiresAt: DateTimeOffset.UtcNow + ttl));
    }
}
