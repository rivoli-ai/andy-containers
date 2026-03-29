using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class AllowAllPolicyProviderTests
{
    [Fact]
    public async Task GetPolicyAsync_ReturnsPermissivePolicy_ForAnyPolicyName()
    {
        var options = Options.Create(new AuthorizationOptions());
        var provider = new AllowAllPolicyProvider(options);

        var policy = await provider.GetPolicyAsync("Permission:container:read");

        policy.Should().NotBeNull();
        policy!.Requirements.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Permission:container:read")]
    [InlineData("Permission:template:write")]
    [InlineData("Permission:provider:admin")]
    [InlineData("SomeRandomPolicy")]
    public async Task GetPolicyAsync_ReturnsNonNull_ForAllPolicyNames(string policyName)
    {
        var options = Options.Create(new AuthorizationOptions());
        var provider = new AllowAllPolicyProvider(options);

        var policy = await provider.GetPolicyAsync(policyName);

        policy.Should().NotBeNull();
    }
}
