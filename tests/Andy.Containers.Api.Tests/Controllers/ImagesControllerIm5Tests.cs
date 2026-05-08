using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models.ImageManagement;
using Andy.Containers.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

/// <summary>
/// #278. Coverage for the IM5 digest-anchored artifact endpoints
/// added on top of <see cref="ImagesController"/>:
///
/// - <c>GET /api/images</c> — paged list with optional filters
/// - <c>GET /api/images/by-digest/{digest}</c> — single artifact lookup
/// - <c>DELETE /api/images/by-digest/{digest}/references/{referenceId}</c> — untag
///
/// The legacy template-keyed `ContainerImage` endpoints stay covered by
/// <see cref="ImagesControllerTests"/>; this file focuses purely on
/// the new BuildArtifact-keyed surface.
/// </summary>
public class ImagesControllerIm5Tests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IBuildArtifactStore> _mockArtifactStore;
    private readonly ImagesController _controller;

    public ImagesControllerIm5Tests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockArtifactStore = new Mock<IBuildArtifactStore>();

        var mockCurrentUser = new Mock<ICurrentUserService>();
        mockCurrentUser.Setup(u => u.GetUserId()).Returns("test-user");
        mockCurrentUser.Setup(u => u.IsAdmin()).Returns(true);
        mockCurrentUser.Setup(u => u.IsAuthenticated()).Returns(true);
        var mockOrg = new Mock<IOrganizationMembershipService>();

        _controller = new ImagesController(
            _db,
            new Mock<IImageManifestService>().Object,
            new Mock<IImageDiffService>().Object,
            mockCurrentUser.Object,
            mockOrg.Object,
            new Mock<IImageBuildOrchestrator>().Object,
            new Mock<IAsyncBuildExecutor>().Object,
            new Mock<IBuildEventBus>().Object,
            new Mock<IBuildExecutionRegistry>().Object,
            _mockArtifactStore.Object);
    }

    public void Dispose() => _db.Dispose();

    // -------------------------------------------------------------
    // GET /api/images
    // -------------------------------------------------------------

    [Fact]
    public async Task ListArtifacts_ReturnsPagedShape()
    {
        var templateId = Guid.NewGuid();
        var artifact = MakeArtifact(templateId, digest: "sha256:abc");
        artifact.References.Add(MakeReference("local-zot", "code/test", "v1"));

        _mockArtifactStore.Setup(s => s.ListAsync(null, null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<BuildArtifactEntity> { artifact }, 1));

        var result = await _controller.ListArtifacts(
            templateId: null,
            registryId: null,
            marker: null,
            skip: 0,
            take: 20,
            ct: CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var page = ok.Value.Should().BeOfType<BuildArtifactListResponse>().Subject;
        page.TotalCount.Should().Be(1);
        page.Items.Should().HaveCount(1);
        var item = page.Items[0];
        item.Digest.Should().Be("sha256:abc");
        item.TemplateId.Should().Be(templateId);
        item.References.Should().HaveCount(1, "the IM5 BuildArtifact shape carries the references list inline.");
    }

    [Fact]
    public async Task ListArtifacts_PassesFiltersToStore()
    {
        var templateId = Guid.NewGuid();
        _mockArtifactStore.Setup(s => s.ListAsync(templateId, "local-zot", 5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<BuildArtifactEntity>(), 0))
            .Verifiable();

        var result = await _controller.ListArtifacts(
            templateId: templateId,
            registryId: "local-zot",
            marker: null,
            skip: 5,
            take: 10,
            ct: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _mockArtifactStore.Verify();
    }

    [Fact]
    public async Task ListArtifacts_ClampsTakeToMax()
    {
        // Take is clamped to 100 to keep a single request from pulling
        // arbitrary amounts of data. Anything above gets capped silently
        // — callers paginate normally and the contract still holds.
        _mockArtifactStore.Setup(s => s.ListAsync(null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<BuildArtifactEntity>(), 0))
            .Verifiable();

        var result = await _controller.ListArtifacts(null, null, null, skip: 0, take: 5000, ct: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _mockArtifactStore.Verify();
    }

    [Fact]
    public async Task ListArtifacts_NegativeSkipNormalisesToZero()
    {
        _mockArtifactStore.Setup(s => s.ListAsync(null, null, 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<BuildArtifactEntity>(), 0))
            .Verifiable();

        var result = await _controller.ListArtifacts(null, null, null, skip: -10, take: 20, ct: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _mockArtifactStore.Verify();
    }

    [Fact]
    public async Task ListArtifacts_MarkerFilter_ReturnsBadRequest()
    {
        // marker is OpenAPI-declared but not yet implemented. Refuse
        // explicitly so callers don't get unfiltered results back and
        // mistake them for a marker match.
        var result = await _controller.ListArtifacts(
            templateId: null,
            registryId: null,
            marker: "baked-assistants:claude-code",
            skip: 0,
            take: 20,
            ct: CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = bad.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().Be("image.list.marker.unsupported");
    }

    // -------------------------------------------------------------
    // GET /api/images/by-digest/{digest}
    // -------------------------------------------------------------

    [Fact]
    public async Task GetByDigest_ReturnsArtifact()
    {
        var templateId = Guid.NewGuid();
        var artifact = MakeArtifact(templateId, digest: "sha256:abc");
        artifact.References.Add(MakeReference("local-zot", "code/test", "v1"));

        _mockArtifactStore.Setup(s => s.GetByDigestAsync("sha256:abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);

        var result = await _controller.GetByDigest("sha256:abc", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<BuildArtifactResponse>().Subject;
        response.Digest.Should().Be("sha256:abc");
        response.References.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByDigest_NotFoundShape_Is404WithImageManagementError()
    {
        _mockArtifactStore.Setup(s => s.GetByDigestAsync("sha256:nope", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildArtifactEntity?)null);

        var result = await _controller.GetByDigest("sha256:nope", CancellationToken.None);

        var notFound = result.Should().BeOfType<ObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);
        var body = notFound.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().Be("image.not-found");
    }

    [Fact]
    public async Task GetByDigest_EmptyDigestPath_Returns400()
    {
        var result = await _controller.GetByDigest("", CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = bad.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().Be("image.digest.required");
    }

    // -------------------------------------------------------------
    // DELETE /api/images/by-digest/{digest}/references/{referenceId}
    // -------------------------------------------------------------

    [Fact]
    public async Task Untag_RemovesReferenceAndReturns204()
    {
        var templateId = Guid.NewGuid();
        var refId = Guid.NewGuid();
        var artifact = MakeArtifact(templateId, digest: "sha256:abc");
        var reference = MakeReference("local-zot", "code/test", "v1");
        reference.Id = refId;
        artifact.References.Add(reference);

        _mockArtifactStore.Setup(s => s.GetByDigestAsync("sha256:abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);
        _mockArtifactStore.Setup(s => s.RemoveReferenceAsync(refId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var result = await _controller.Untag("sha256:abc", refId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _mockArtifactStore.Verify();
    }

    [Fact]
    public async Task Untag_AlreadyGoneReference_IsIdempotent204()
    {
        var refId = Guid.NewGuid();
        var artifact = MakeArtifact(Guid.NewGuid(), digest: "sha256:abc");
        // No reference with the requested id on this artifact.

        _mockArtifactStore.Setup(s => s.GetByDigestAsync("sha256:abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);

        var result = await _controller.Untag("sha256:abc", refId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>(
            "removing an already-gone reference is not an error per the IM5 OpenAPI.");
        _mockArtifactStore.Verify(s => s.RemoveReferenceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "no DB write should fire when the reference doesn't exist on the artifact.");
    }

    [Fact]
    public async Task Untag_DigestNotFound_Returns404()
    {
        var refId = Guid.NewGuid();
        _mockArtifactStore.Setup(s => s.GetByDigestAsync("sha256:nope", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BuildArtifactEntity?)null);

        var result = await _controller.Untag("sha256:nope", refId, CancellationToken.None);

        var notFound = result.Should().BeOfType<ObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);
        var body = notFound.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().Be("image.not-found");
    }

    [Fact]
    public async Task Untag_RefBelongsToDifferentDigest_Returns204_WithoutDelete()
    {
        // Defence in depth: a request shaped as
        // /by-digest/sha256:A/references/<id-of-ref-on-sha256:B> should
        // NOT silently delete the wrong reference. The artifact for
        // sha256:A is loaded; the reference id isn't in its References
        // collection; we 204 (idempotent) and skip the store call.
        var refId = Guid.NewGuid();
        var artifactA = MakeArtifact(Guid.NewGuid(), digest: "sha256:A");
        // refId belongs to a different artifact, so it's NOT in artifactA.References.

        _mockArtifactStore.Setup(s => s.GetByDigestAsync("sha256:A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifactA);

        var result = await _controller.Untag("sha256:A", refId, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _mockArtifactStore.Verify(s => s.RemoveReferenceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Untag_EmptyDigest_Returns400()
    {
        var result = await _controller.Untag("", Guid.NewGuid(), CancellationToken.None);
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = bad.Value.Should().BeOfType<ImageManagementErrorBody>().Subject;
        body.Code.Should().Be("image.digest.required");
    }

    // -------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------

    private static BuildArtifactEntity MakeArtifact(Guid templateId, string digest)
    {
        return new BuildArtifactEntity
        {
            Id = Guid.NewGuid(),
            Digest = digest,
            MediaType = "application/vnd.oci.image.manifest.v1+json",
            SizeBytes = 1234,
            SpecHash = "sha256:" + new string('s', 64),
            TemplateId = templateId,
            BuildBackendId = "local-docker",
            BuiltBy = "test-user",
            BuiltAt = DateTime.UtcNow,
        };
    }

    private static RegistryReferenceEntity MakeReference(string registryId, string repoPath, string tag)
    {
        return new RegistryReferenceEntity
        {
            Id = Guid.NewGuid(),
            RegistryId = registryId,
            RepoPath = repoPath,
            Tag = tag,
            PushedAt = DateTime.UtcNow,
            PushedBy = "test-user",
        };
    }
}
