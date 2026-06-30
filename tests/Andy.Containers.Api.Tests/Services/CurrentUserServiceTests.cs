using System.Security.Claims;
using Andy.Containers.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// Guards the M2M-vs-human discriminator behind on-behalf-of container
/// ownership (the 403/UI fix): only a trusted client_credentials service may
/// create a container owned by another user.
/// </summary>
public class CurrentUserServiceTests
{
    private static CurrentUserService Build(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "Bearer");
        var principal = new ClaimsPrincipal(identity);
        var ctx = new DefaultHttpContext { User = principal };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(ctx);
        return new CurrentUserService(accessor.Object);
    }

    [Fact]
    public void ServiceToken_SubEqualsClientId_IsServiceAccount()
    {
        // OpenIddict client_credentials: subject == client id, no human behind it.
        var sut = Build(
            new Claim("sub", "andy-tasks-api"),
            new Claim("client_id", "andy-tasks-api"));

        sut.IsServiceAccount().Should().BeTrue();
    }

    [Fact]
    public void ServiceToken_NoHumanIdentityClaims_IsServiceAccount()
    {
        // Fallback path: authenticated, but no email/name/preferred_username.
        var sut = Build(new Claim("sub", "andy-tasks-api"));

        sut.IsServiceAccount().Should().BeTrue();
    }

    [Fact]
    public void HumanToken_WithEmail_IsNotServiceAccount()
    {
        var sut = Build(
            new Claim("sub", "user-guid-123"),
            new Claim("client_id", "conductor-web"),
            new Claim("email", "sam@rivoli.ai"));

        sut.IsServiceAccount().Should().BeFalse();
    }

    [Fact]
    public void HumanToken_WithNameOnly_IsNotServiceAccount()
    {
        var sut = Build(
            new Claim("sub", "user-guid-123"),
            new Claim("name", "Sami"));

        sut.IsServiceAccount().Should().BeFalse();
    }

    [Fact]
    public void Unauthenticated_IsNotServiceAccount()
    {
        // Anonymous identity (no authenticationType) is not authenticated.
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(ctx);
        var sut = new CurrentUserService(accessor.Object);

        sut.IsServiceAccount().Should().BeFalse();
    }
}
