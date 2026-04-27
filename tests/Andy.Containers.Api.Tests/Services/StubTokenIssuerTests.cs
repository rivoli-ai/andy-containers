using Andy.Containers.Api.Services;
using Andy.Containers.Configurator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

// AP10 (#112). The stub issuer is the local stand-in until Y6 ships,
// but the runner depends on its observable behaviour: idempotent mint,
// distinct tokens across runs, revoke removes the registration. Pinning
// these here means the eventual Y6 HTTP client only has to satisfy the
// same contract, no surprises at swap-in time.
public class StubTokenIssuerTests
{
    [Fact]
    public async Task MintAsync_ReturnsTokenWithFutureExpiry()
    {
        var issuer = new StubTokenIssuer(NullLogger<StubTokenIssuer>.Instance);

        var token = await issuer.MintAsync(Guid.NewGuid());

        token.Token.Should().StartWith("andy-run.",
            "the prefix lets log scrapers identify run-scoped tokens distinct from session ones");
        token.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task MintAsync_DistinctRuns_GetDistinctTokens()
    {
        var issuer = new StubTokenIssuer(NullLogger<StubTokenIssuer>.Instance);

        var a = await issuer.MintAsync(Guid.NewGuid());
        var b = await issuer.MintAsync(Guid.NewGuid());

        b.Token.Should().NotBe(a.Token);
    }

    [Fact]
    public async Task MintAsync_IsIdempotentForSameRunId()
    {
        // Configurator may run twice (initial + retry). The same Run.Id
        // must not produce two tokens — the second call returns the
        // existing one.
        var issuer = new StubTokenIssuer(NullLogger<StubTokenIssuer>.Instance);
        var runId = Guid.NewGuid();

        var first = await issuer.MintAsync(runId);
        var second = await issuer.MintAsync(runId);

        second.Should().Be(first);
    }

    [Fact]
    public async Task RevokeAsync_RemovesRegistration_AndReturnsTrue()
    {
        var issuer = new StubTokenIssuer(NullLogger<StubTokenIssuer>.Instance);
        var runId = Guid.NewGuid();
        await issuer.MintAsync(runId);

        var revoked = await issuer.RevokeAsync(runId);

        revoked.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeAsync_UnknownRunId_ReturnsFalse()
    {
        // Server-restart / double-revoke / never-minted all share this
        // path: caller treats false as "post-condition holds anyway".
        var issuer = new StubTokenIssuer(NullLogger<StubTokenIssuer>.Instance);

        var revoked = await issuer.RevokeAsync(Guid.NewGuid());

        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_AfterRevoke_AllowsFreshMint()
    {
        // Once revoked, re-minting for the same Run.Id (e.g. a re-submit
        // flow) must produce a new token. The dictionary slot is free.
        var issuer = new StubTokenIssuer(NullLogger<StubTokenIssuer>.Instance);
        var runId = Guid.NewGuid();
        var first = await issuer.MintAsync(runId);
        await issuer.RevokeAsync(runId);

        var fresh = await issuer.MintAsync(runId);

        fresh.Token.Should().NotBe(first.Token);
    }
}
