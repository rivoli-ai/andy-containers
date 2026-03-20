using System.Security.Claims;
using Andy.Containers.Api.Services;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class OrganizationMembershipServiceTests
{
    private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor;
    private readonly IMemoryCache _cache;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<ILogger<OrganizationMembershipService>> _mockLogger;
    private readonly OrganizationMembershipService _service;

    private static readonly Guid TestOrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherOrgId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public OrganizationMembershipServiceTests()
    {
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        // Create a mock HttpClient that returns failure by default (simulates RBAC API unavailable)
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:5300") };
        _mockHttpClientFactory.Setup(f => f.CreateClient("AndyRbac")).Returns(httpClient);
        _mockLogger = new Mock<ILogger<OrganizationMembershipService>>();
        _service = new OrganizationMembershipService(
            _mockHttpContextAccessor.Object,
            _cache,
            _mockHttpClientFactory.Object,
            _mockLogger.Object);
    }

    private void SetupClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);
    }

    [Fact]
    public async Task IsMemberAsync_WithMatchingOrgIdClaim_ShouldReturnTrue()
    {
        SetupClaims(new Claim("org_id", TestOrgId.ToString()));

        var result = await _service.IsMemberAsync("user1", TestOrgId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsMemberAsync_WithOrgIdsClaimContainingOrg_ShouldReturnTrue()
    {
        SetupClaims(new Claim("org_ids", $"{TestOrgId},{OtherOrgId}"));

        var result = await _service.IsMemberAsync("user1", TestOrgId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsMemberAsync_WithOrgIdsClaimNotContainingOrg_ShouldReturnFalse()
    {
        SetupClaims(new Claim("org_ids", OtherOrgId.ToString()));

        var result = await _service.IsMemberAsync("user1", TestOrgId);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsMemberAsync_NoMatchingClaims_ShouldFallbackToApi()
    {
        SetupClaims(); // No org claims

        var result = await _service.IsMemberAsync("user1", TestOrgId);

        // API returns NotFound by default, so false
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsMemberAsync_CachesResult()
    {
        SetupClaims(new Claim("org_id", TestOrgId.ToString()));

        var result1 = await _service.IsMemberAsync("user1", TestOrgId);
        // Clear claims to verify cache is used
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);
        var result2 = await _service.IsMemberAsync("user1", TestOrgId);

        result1.Should().BeTrue();
        result2.Should().BeTrue();
    }

    [Fact]
    public async Task GetRoleAsync_WithOrgRoleClaim_ShouldReturnRole()
    {
        SetupClaims(
            new Claim("org_id", TestOrgId.ToString()),
            new Claim("org_role", OrgRoles.Admin));

        var result = await _service.GetRoleAsync("user1", TestOrgId);

        result.Should().Be(OrgRoles.Admin);
    }

    [Fact]
    public async Task GetRoleAsync_DifferentOrg_ShouldFallbackToApi()
    {
        SetupClaims(
            new Claim("org_id", OtherOrgId.ToString()),
            new Claim("org_role", OrgRoles.Admin));

        var result = await _service.GetRoleAsync("user1", TestOrgId);

        // API returns NotFound, so null
        result.Should().BeNull();
    }

    [Fact]
    public async Task HasPermissionAsync_AdminRole_ShouldReturnTrueForAnyPermission()
    {
        SetupClaims(
            new Claim("org_id", TestOrgId.ToString()),
            new Claim("org_role", OrgRoles.Admin));

        var result = await _service.HasPermissionAsync("user1", TestOrgId, Permissions.ImageDelete);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermissionAsync_ViewerRole_ShouldReturnFalseForManagePermission()
    {
        SetupClaims(
            new Claim("org_id", TestOrgId.ToString()),
            new Claim("org_role", OrgRoles.Viewer));

        var result = await _service.HasPermissionAsync("user1", TestOrgId, Permissions.ProviderManage);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasPermissionAsync_ViewerRole_ShouldReturnTrueForReadPermission()
    {
        SetupClaims(
            new Claim("org_id", TestOrgId.ToString()),
            new Claim("org_role", OrgRoles.Viewer));

        var result = await _service.HasPermissionAsync("user1", TestOrgId, Permissions.ImageRead);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermissionAsync_NoRole_ShouldReturnFalse()
    {
        SetupClaims(); // No org claims

        var result = await _service.HasPermissionAsync("user1", TestOrgId, Permissions.ImageRead);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserOrganizationsAsync_WithOrgIdClaim_ShouldReturnOrg()
    {
        SetupClaims(new Claim("org_id", TestOrgId.ToString()));

        var result = await _service.GetUserOrganizationsAsync("user1");

        result.Should().HaveCount(1);
        result.Should().Contain(TestOrgId);
    }

    [Fact]
    public async Task GetUserOrganizationsAsync_WithOrgIdsClaim_ShouldReturnAllOrgs()
    {
        SetupClaims(new Claim("org_ids", $"{TestOrgId},{OtherOrgId}"));

        var result = await _service.GetUserOrganizationsAsync("user1");

        result.Should().HaveCount(2);
        result.Should().Contain(TestOrgId);
        result.Should().Contain(OtherOrgId);
    }

    [Fact]
    public async Task GetUserOrganizationsAsync_WithBothClaims_ShouldDeduplicateOrgs()
    {
        SetupClaims(
            new Claim("org_id", TestOrgId.ToString()),
            new Claim("org_ids", $"{TestOrgId},{OtherOrgId}"));

        var result = await _service.GetUserOrganizationsAsync("user1");

        result.Should().HaveCount(2);
        result.Should().Contain(TestOrgId);
        result.Should().Contain(OtherOrgId);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public MockHttpMessageHandler(HttpResponseMessage response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
