using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andy.Containers.Api.Tests.Controllers;

// X3 (rivoli-ai/andy-containers#92). Read-only catalog endpoint:
// list / get-by-id / get-by-code, with kind filter + pagination on
// list. Tests use direct controller instantiation against an in-memory
// DbContext, matching WorkspacesControllerTests / RunsControllerTests.
// Auth-pipeline behaviour ([RequirePermission]) is exercised by the
// Andy.Rbac filter tests; here we cover controller logic only.
public class EnvironmentsControllerTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly EnvironmentsController _controller;

    public EnvironmentsControllerTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _controller = new EnvironmentsController(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task List_ReturnsAllSeededProfiles_WithTotal()
    {
        SeedThreeProfiles();

        var result = await _controller.List(kind: null, skip: 0, take: 50);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var (items, total) = ExtractPage(ok.Value!);
        total.Should().Be(3);
        items.Select(p => p.Code).Should().BeEquivalentTo(new[]
        {
            "desktop", "headless-container", "terminal",
        });
    }

    [Fact]
    public async Task List_OrdersByCode()
    {
        SeedThreeProfiles();

        var result = await _controller.List(kind: null, skip: 0, take: 50);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var (items, _) = ExtractPage(ok.Value!);
        items.Select(p => p.Code).Should().ContainInOrder(
            "desktop", "headless-container", "terminal");
    }

    [Theory]
    [InlineData("HeadlessContainer", "headless-container")]
    [InlineData("headlesscontainer", "headless-container")]   // case-insensitive
    [InlineData("Terminal", "terminal")]
    [InlineData("Desktop", "desktop")]
    public async Task List_KindFilter_NarrowsToMatchingProfiles(string kind, string expectedCode)
    {
        SeedThreeProfiles();

        var result = await _controller.List(kind: kind, skip: 0, take: 50);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var (items, total) = ExtractPage(ok.Value!);
        total.Should().Be(1);
        items.Single().Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task List_UnknownKind_ReturnsBadRequest()
    {
        SeedThreeProfiles();

        var result = await _controller.List(kind: "AlienShape", skip: 0, take: 50);

        result.Should().BeOfType<BadRequestObjectResult>(
            "an unknown kind is a typo, not an empty result — surface it");
    }

    [Fact]
    public async Task List_PaginationCapsTake_AndClampsSkip()
    {
        SeedThreeProfiles();

        // skip < 0 must clamp to 0; take > 200 must clamp to 50; total
        // still reflects the unfiltered count.
        var result = await _controller.List(kind: null, skip: -5, take: 9999);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var (items, total) = ExtractPage(ok.Value!);
        total.Should().Be(3);
        items.Should().HaveCount(3);
    }

    [Fact]
    public async Task Get_ExistingId_ReturnsDtoWithStructuredCapabilities()
    {
        var seeded = SeedThreeProfiles();
        var headless = seeded.Single(p => p.Name == "headless-container");

        var result = await _controller.Get(headless.Id, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<EnvironmentProfileDto>().Subject;
        dto.Code.Should().Be("headless-container");
        dto.Kind.Should().Be("HeadlessContainer");
        dto.Capabilities.HasGui.Should().BeFalse();
        dto.Capabilities.SecretsScope.Should().Be(SecretsScope.WorkspaceScoped);
        dto.Capabilities.AuditMode.Should().Be(AuditMode.Strict);
        dto.Capabilities.NetworkAllowlist.Should().Contain("registry.rivoli.ai");
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        var result = await _controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetByCode_ExistingCode_ReturnsDto()
    {
        SeedThreeProfiles();

        var result = await _controller.GetByCode("desktop", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<EnvironmentProfileDto>().Subject;
        dto.Capabilities.HasGui.Should().BeTrue();
        dto.Capabilities.SecretsScope.Should().Be(SecretsScope.OrganizationScoped);
    }

    [Fact]
    public async Task GetByCode_UnknownCode_ReturnsNotFound()
    {
        SeedThreeProfiles();

        var result = await _controller.GetByCode("nonexistent", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetByCode_BlankCode_ReturnsNotFound()
    {
        var result = await _controller.GetByCode("   ", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>(
            "blank input is treated as a missing-row lookup, not a 400");
    }

    private List<EnvironmentProfile> SeedThreeProfiles()
    {
        var profiles = new[]
        {
            new EnvironmentProfile
            {
                Id = Guid.NewGuid(),
                Name = "headless-container",
                DisplayName = "Headless container",
                Kind = EnvironmentKind.HeadlessContainer,
                BaseImageRef = "ghcr.io/rivoli-ai/andy-headless:latest",
                Capabilities = new EnvironmentCapabilities
                {
                    NetworkAllowlist = new List<string>
                    {
                        "registry.rivoli.ai", "api.github.com", "pypi.org", "nuget.org",
                    },
                    SecretsScope = SecretsScope.WorkspaceScoped,
                    HasGui = false,
                    AuditMode = AuditMode.Strict,
                },
            },
            new EnvironmentProfile
            {
                Id = Guid.NewGuid(),
                Name = "terminal",
                DisplayName = "Terminal session",
                Kind = EnvironmentKind.Terminal,
                BaseImageRef = "ghcr.io/rivoli-ai/andy-terminal:latest",
                Capabilities = new EnvironmentCapabilities
                {
                    NetworkAllowlist = new List<string> { "*" },
                    SecretsScope = SecretsScope.WorkspaceScoped,
                    HasGui = false,
                    AuditMode = AuditMode.Standard,
                },
            },
            new EnvironmentProfile
            {
                Id = Guid.NewGuid(),
                Name = "desktop",
                DisplayName = "Desktop session",
                Kind = EnvironmentKind.Desktop,
                BaseImageRef = "ghcr.io/rivoli-ai/andy-desktop:latest",
                Capabilities = new EnvironmentCapabilities
                {
                    NetworkAllowlist = new List<string> { "*" },
                    SecretsScope = SecretsScope.OrganizationScoped,
                    HasGui = true,
                    AuditMode = AuditMode.Standard,
                },
            },
        };

        _db.EnvironmentProfiles.AddRange(profiles);
        _db.SaveChanges();
        return profiles.ToList();
    }

    // List returns an anonymous { items, totalCount } envelope; reflect
    // the two fields rather than asserting on the anonymous type so the
    // tests stay readable.
    private static (List<EnvironmentProfileDto> Items, int Total) ExtractPage(object payload)
    {
        var t = payload.GetType();
        var items = (IEnumerable<EnvironmentProfileDto>)t.GetProperty("items")!.GetValue(payload)!;
        var total = (int)t.GetProperty("totalCount")!.GetValue(payload)!;
        return (items.ToList(), total);
    }
}
