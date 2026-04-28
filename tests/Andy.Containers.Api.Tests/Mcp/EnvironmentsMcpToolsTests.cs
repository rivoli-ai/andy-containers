using Andy.Containers.Api.Mcp;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Mcp;

// X6 (rivoli-ai/andy-containers#96). The MCP environment.list tool is
// the discovery surface for the EnvironmentProfile catalog. Tests pin
// the contract that matters: permission gating, kind filtering, and
// the structured Capabilities block reaching MCP consumers without an
// extra HTTP round-trip.
public class EnvironmentsMcpToolsTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<ICurrentUserService> _currentUser;
    private readonly Mock<IOrganizationMembershipService> _orgMembership;
    private readonly EnvironmentsMcpTools _tools;
    private readonly Guid _orgId = Guid.NewGuid();

    public EnvironmentsMcpToolsTests()
    {
        _db = InMemoryDbHelper.CreateContext();

        _currentUser = new Mock<ICurrentUserService>();
        _currentUser.Setup(u => u.GetUserId()).Returns("test-user");
        _currentUser.Setup(u => u.IsAdmin()).Returns(false);
        _currentUser.Setup(u => u.GetOrganizationId()).Returns(_orgId);

        _orgMembership = new Mock<IOrganizationMembershipService>();
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _tools = new EnvironmentsMcpTools(
            _db, _currentUser.Object, _orgMembership.Object,
            NullLogger<EnvironmentsMcpTools>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ListEnvironments_NoFilter_ReturnsAllProfiles_OrderedByCode()
    {
        SeedThreeProfiles();

        var result = await _tools.ListEnvironments();

        result.Should().HaveCount(3);
        result.Select(p => p.Code).Should().ContainInOrder(
            "desktop", "headless-container", "terminal");
    }

    [Fact]
    public async Task ListEnvironments_ReturnsStructuredCapabilityBlock()
    {
        SeedThreeProfiles();

        var result = await _tools.ListEnvironments();

        var headless = result.Single(p => p.Code == "headless-container");
        headless.Kind.Should().Be("HeadlessContainer");
        headless.BaseImageRef.Should().Be("ghcr.io/rivoli-ai/andy-headless:latest");
        headless.Capabilities.HasGui.Should().BeFalse();
        headless.Capabilities.SecretsScope.Should().Be(SecretsScope.WorkspaceScoped);
        headless.Capabilities.AuditMode.Should().Be(AuditMode.Strict);
        headless.Capabilities.NetworkAllowlist.Should().Contain("registry.rivoli.ai");
    }

    [Theory]
    [InlineData("HeadlessContainer", "headless-container")]
    [InlineData("headlesscontainer", "headless-container")] // case-insensitive
    [InlineData("Terminal", "terminal")]
    [InlineData("Desktop", "desktop")]
    public async Task ListEnvironments_KindFilter_NarrowsToMatchingProfile(string kind, string expected)
    {
        SeedThreeProfiles();

        var result = await _tools.ListEnvironments(kind);

        result.Should().ContainSingle().Which.Code.Should().Be(expected);
    }

    [Fact]
    public async Task ListEnvironments_UnknownKind_ReturnsEmpty()
    {
        SeedThreeProfiles();

        var result = await _tools.ListEnvironments(kind: "AlienShape");

        result.Should().BeEmpty(
            "MCP contract is 'list': an unknown kind narrows the result, the HTTP layer surfaces the typo");
    }

    [Fact]
    public async Task ListEnvironments_NonAdminWithoutEnvironmentRead_ReturnsEmpty()
    {
        SeedThreeProfiles();
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), _orgId, Permissions.EnvironmentRead, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _tools.ListEnvironments();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListEnvironments_AdminBypassesOrgPermissionLookup()
    {
        SeedThreeProfiles();
        _currentUser.Setup(u => u.IsAdmin()).Returns(true);
        _orgMembership
            .Setup(o => o.HasPermissionAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _tools.ListEnvironments();

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListEnvironments_NoPrimaryOrg_ReturnsEmpty()
    {
        // Edge: a user with no primary org can't have org-scoped
        // permissions evaluated. Match the convention RunsMcpTools
        // uses (return empty rather than throw) so the MCP transport
        // surfaces "no results visible" instead of a noisy error.
        SeedThreeProfiles();
        _currentUser.Setup(u => u.GetOrganizationId()).Returns((Guid?)null);

        var result = await _tools.ListEnvironments();

        result.Should().BeEmpty();
    }

    private void SeedThreeProfiles()
    {
        _db.EnvironmentProfiles.AddRange(
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
            });
        _db.SaveChanges();
    }
}
