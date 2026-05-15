using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// rivoli-ai/conductor#943 (M1.5.1). Covers the per-container
/// proxy-token path in
/// <see cref="ContainerOrchestrationService.CreateContainerAsync"/>
/// and <see cref="ContainerOrchestrationService.DestroyContainerAsync"/>:
/// minting, persistence on the <see cref="Container"/> row,
/// fail-fast on andy-models errors, and revoke-on-destroy.
/// </summary>
public class ContainerOrchestrationServiceProxyTokenTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureRoutingService> _mockRouting = new();
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory = new();
    private readonly Mock<IInfrastructureProvider> _mockInfra = new();
    private readonly ContainerProvisioningQueue _queue = new();
    private readonly Mock<IGitRepositoryProbeService> _mockProbe = new();
    private readonly EphemeralDataProtectionProvider _dataProtection = new();

    public ContainerOrchestrationServiceProxyTokenTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>()))
            .Returns(_mockInfra.Object);
        _mockProbe.Setup(p => p.ProbeRepositoriesAsync(
                It.IsAny<IReadOnlyList<GitRepositoryConfig>>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
    }

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // Happy path: per-container token wins over shared M2M
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateContainer_WithClaudeCodeAssistant_MintsPerContainerTokenAndPersists()
    {
        var (template, provider) = await SeedAsync();
        var proxyService = new StubProxyTokenService(
            mintResult: new MintedProxyToken(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "per.container.jwt",
                DateTimeOffset.Parse("2026-06-01T00:00:00Z")));
        var sharedTokens = new StubServiceTokenService("shared-m2m-must-not-be-used");
        var service = MakeService(sharedTokens, proxyService);

        var created = await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "claude-container",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-42",
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode },
        }, CancellationToken.None);

        // Persisted on Container row
        var fromDb = await _db.Containers.FindAsync(created.Id);
        fromDb.Should().NotBeNull();
        fromDb!.ProxyServiceTokenId.Should().Be(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        fromDb.ProxyTokenIssuedAt.Should().NotBeNull();
        fromDb.ProxyServiceToken.Should().NotBeNullOrEmpty();
        fromDb.ProxyServiceToken.Should().NotBe("per.container.jwt",
            "the persisted token must be encrypted at rest, not the raw JWT.");
        // Round-trip through the same protector recovers the JWT
        var protector = _dataProtection.CreateProtector("Container.ProxyServiceToken");
        protector.Unprotect(fromDb.ProxyServiceToken!).Should().Be("per.container.jwt");

        // Injected as ANDY_SERVICE_TOKEN, NOT the shared M2M
        var job = await ReadEnqueuedJobAsync();
        job.EnvironmentVariables.Should().NotBeNull();
        job.EnvironmentVariables!["ANDY_SERVICE_TOKEN"].Should().Be("per.container.jwt");

        // Mint call carried the right shape
        proxyService.LastMintCall.Should().NotBeNull();
        proxyService.LastMintCall!.Value.containerId.Should().Be(created.Id.ToString());
        proxyService.LastMintCall.Value.subjectId.Should().Be("user-42");
        proxyService.LastMintCall.Value.slugs.Should().Equal("anthropic/claude-sonnet-4-6");
    }

    [Fact]
    public async Task CreateContainer_WithExplicitRequiredSlugs_UsesThemInsteadOfDefault()
    {
        var (template, provider) = await SeedAsync();
        var proxyService = new StubProxyTokenService(
            mintResult: new MintedProxyToken(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "scoped.jwt",
                DateTimeOffset.UtcNow.AddHours(1)));
        var service = MakeService(new StubServiceTokenService("shared-tok"), proxyService);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "custom-slugs",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-1",
            CodeAssistant = new CodeAssistantConfig
            {
                Tool = CodeAssistantType.ClaudeCode,
                RequiredModelSlugs = new List<string> { "anthropic/claude-opus-4", "anthropic/claude-haiku-4-5" },
            },
        }, CancellationToken.None);

        proxyService.LastMintCall!.Value.slugs.Should().Equal(
            "anthropic/claude-opus-4", "anthropic/claude-haiku-4-5");
    }

    // -----------------------------------------------------------------
    // Fall-through paths: shared M2M still works when no slugs
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateContainer_NoCodeAssistant_FallsBackToSharedM2MToken()
    {
        var (template, provider) = await SeedAsync();
        var proxyService = new StubProxyTokenService();
        var service = MakeService(new StubServiceTokenService("shared-m2m-token"), proxyService);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "no-assistant",
            TemplateId = template.Id,
            ProviderId = provider.Id,
        }, CancellationToken.None);

        var job = await ReadEnqueuedJobAsync();
        job.EnvironmentVariables!["ANDY_SERVICE_TOKEN"].Should().Be("shared-m2m-token");
        proxyService.MintCallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateContainer_AssistantWithExplicitEmptySlugs_FallsBackToSharedM2MToken()
    {
        var (template, provider) = await SeedAsync();
        var proxyService = new StubProxyTokenService();
        var service = MakeService(new StubServiceTokenService("shared-tok"), proxyService);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "ollama-style",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            CodeAssistant = new CodeAssistantConfig
            {
                Tool = CodeAssistantType.OpenCode,
                // Explicit empty: "I'm doing my own auth (e.g. Ollama)"
                RequiredModelSlugs = new List<string>(),
            },
        }, CancellationToken.None);

        var job = await ReadEnqueuedJobAsync();
        job.EnvironmentVariables!["ANDY_SERVICE_TOKEN"].Should().Be("shared-tok");
        proxyService.MintCallCount.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Fail-fast: andy-models down for a container that needs a token
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateContainer_AndyModelsUnreachable_ThrowsInvalidOperationException()
    {
        var (template, provider) = await SeedAsync();
        var proxyService = new StubProxyTokenService(
            mintException: new ProxyTokenException("andy-models unreachable at http://andy-models.test"));
        var service = MakeService(new StubServiceTokenService("shared-tok"), proxyService);

        await FluentActions.Awaiting(() => service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "fail-fast",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode },
        }, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not mint per-container proxy token*andy-models*");
    }

    // -----------------------------------------------------------------
    // Revoke on destroy
    // -----------------------------------------------------------------

    [Fact]
    public async Task DestroyContainer_WhenTokenWasMinted_RevokesIt()
    {
        var (template, provider) = await SeedAsync();
        var tokenId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var proxyService = new StubProxyTokenService(
            mintResult: new MintedProxyToken(tokenId, "j.w.t", DateTimeOffset.UtcNow.AddHours(1)));
        var service = MakeService(new StubServiceTokenService("shared-tok"), proxyService);

        var created = await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "destroyable",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode },
        }, CancellationToken.None);

        await service.DestroyContainerAsync(created.Id, CancellationToken.None);

        proxyService.RevokeCalls.Should().ContainSingle().Which.Should().Be(tokenId);

        var afterDestroy = await _db.Containers.FindAsync(created.Id);
        afterDestroy!.ProxyServiceTokenId.Should().BeNull(
            "the Container row's token ref clears on destroy so a future query can't surface a revoked id.");
        afterDestroy.ProxyServiceToken.Should().BeNull();
    }

    [Fact]
    public async Task DestroyContainer_WhenNoTokenWasMinted_DoesNotCallRevoke()
    {
        var (template, provider) = await SeedAsync();
        var proxyService = new StubProxyTokenService();
        var service = MakeService(new StubServiceTokenService("shared-tok"), proxyService);

        var created = await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "no-token-destroy",
            TemplateId = template.Id,
            ProviderId = provider.Id,
        }, CancellationToken.None);

        await service.DestroyContainerAsync(created.Id, CancellationToken.None);

        proxyService.RevokeCalls.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedAsync()
    {
        var template = new ContainerTemplate
        {
            Code = "proxy-tok-test",
            Name = "Proxy token test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
        };
        var provider = new InfrastructureProvider
        {
            Code = "tok-provider",
            Name = "Provider",
            Type = ProviderType.Docker,
            IsEnabled = true,
        };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return (template, provider);
    }

    private ContainerOrchestrationService MakeService(
        IServiceTokenService sharedTokens,
        IProxyTokenService proxyTokens)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return new ContainerOrchestrationService(
            _db,
            _mockRouting.Object,
            _mockFactory.Object,
            _queue,
            _mockProbe.Object,
            config,
            NullLogger<ContainerOrchestrationService>.Instance,
            serviceTokenService: sharedTokens,
            proxyTokenService: proxyTokens,
            dataProtection: _dataProtection);
    }

    private async Task<ContainerProvisionJob> ReadEnqueuedJobAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await _queue.Reader.ReadAsync(cts.Token);
    }

    private sealed class StubServiceTokenService : IServiceTokenService
    {
        private readonly string _token;
        public StubServiceTokenService(string token) { _token = token; }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(_token);
        public Task<string> GetAccessTokenAsync(string audience, CancellationToken ct = default) => Task.FromResult(_token);
        public Task<string> GetOnBehalfOfTokenAsync(string subjectToken, string audience, CancellationToken ct = default) => Task.FromResult(_token);
    }

    /// <summary>
    /// Configurable stub: mint returns a fixed result (or null), or
    /// throws a fixed exception. Records every mint call's args + every
    /// revoke call's tokenId for assertions.
    /// </summary>
    private sealed class StubProxyTokenService : IProxyTokenService
    {
        private readonly MintedProxyToken? _mintResult;
        private readonly ProxyTokenException? _mintException;

        public List<Guid> RevokeCalls { get; } = new();
        public int MintCallCount { get; private set; }
        public (string containerId, string subjectId, IReadOnlyList<string> slugs)? LastMintCall { get; private set; }

        public StubProxyTokenService(MintedProxyToken? mintResult = null, ProxyTokenException? mintException = null)
        {
            _mintResult = mintResult;
            _mintException = mintException;
        }

        public Task<MintedProxyToken?> MintForContainerAsync(
            string containerId,
            string subjectId,
            IReadOnlyList<string> allowedSlugs,
            CancellationToken ct = default)
        {
            MintCallCount++;
            LastMintCall = (containerId, subjectId, allowedSlugs);
            if (_mintException is not null)
            {
                return Task.FromException<MintedProxyToken?>(_mintException);
            }
            return Task.FromResult(_mintResult);
        }

        public Task RevokeAsync(Guid tokenId, CancellationToken ct = default)
        {
            RevokeCalls.Add(tokenId);
            return Task.CompletedTask;
        }
    }
}
