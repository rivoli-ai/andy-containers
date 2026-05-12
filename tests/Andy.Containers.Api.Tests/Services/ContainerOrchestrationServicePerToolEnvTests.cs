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
/// rivoli-ai/conductor#944 (M1.5.2). Integration coverage for the
/// per-tool proxy env-var injection done by
/// <see cref="ContainerOrchestrationService.CreateContainerAsync"/>:
///
/// - Claude Code: ANTHROPIC_API_KEY (= service token) + ANTHROPIC_BASE_URL
///   (= proxy /anthropic/v1)
/// - OpenCode (default backend): OPENAI_API_KEY + OPENAI_BASE_URL
///   pointing at the proxy
/// - OpenCode + user base URL: NO proxy env vars, user URL forwarded
///   instead (Ollama / OpenAI-compatible)
/// - Tools without a proxy routing entry (Continue, GitHub Copilot)
///   leave the credential-path env vars alone
///
/// Drift in any of these silently re-routes traffic around the proxy
/// — the user loses the UsageEvent log and andy-models' key resolver
/// stops being load-bearing.
/// </summary>
public class ContainerOrchestrationServicePerToolEnvTests : IDisposable
{
    private const string ProxyBase = "http://host.docker.internal:9100";

    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureRoutingService> _mockRouting = new();
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory = new();
    private readonly Mock<IInfrastructureProvider> _mockInfra = new();
    private readonly ContainerProvisioningQueue _queue = new();
    private readonly Mock<IGitRepositoryProbeService> _mockProbe = new();
    private readonly EphemeralDataProtectionProvider _dataProtection = new();

    public ContainerOrchestrationServicePerToolEnvTests()
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

    [Fact]
    public async Task CreateContainer_ClaudeCode_SetsAnthropicEnvVarsToProxy()
    {
        var (template, provider) = await SeedAsync();
        var proxy = StubMintingToken("anthropic.jwt");
        var service = MakeService(proxy);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "claude-proxy",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-1",
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode },
        }, CancellationToken.None);

        var env = (await ReadEnqueuedJobAsync()).EnvironmentVariables;
        env.Should().NotBeNull();
        env!["ANTHROPIC_API_KEY"].Should().Be("anthropic.jwt",
            "Claude Code reads ANTHROPIC_API_KEY for its bearer — the per-container service token must replace the user's real Anthropic key.");
        env["ANTHROPIC_BASE_URL"].Should().Be(
            "http://host.docker.internal:9100/models/anthropic/v1",
            "Claude Code reads ANTHROPIC_BASE_URL to pick its server — must route through the andy-models proxy.");
        env["ANDY_SERVICE_TOKEN"].Should().Be("anthropic.jwt",
            "the generic ANDY_SERVICE_TOKEN must still land for non-Claude callers (CodeAssistantInstallService scripts).");
    }

    [Fact]
    public async Task CreateContainer_OpenCodeWithoutBaseUrl_SetsOpenAIEnvVarsToProxy()
    {
        var (template, provider) = await SeedAsync();
        var proxy = StubMintingToken("opencode.jwt");
        var service = MakeService(proxy);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "opencode-default",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-1",
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.OpenCode },
        }, CancellationToken.None);

        var env = (await ReadEnqueuedJobAsync()).EnvironmentVariables;
        env.Should().NotBeNull();
        env!["OPENAI_API_KEY"].Should().Be("opencode.jwt");
        env["OPENAI_BASE_URL"].Should().Be(
            "http://host.docker.internal:9100/models/openai/v1");
    }

    [Fact]
    public async Task CreateContainer_OpenCodeWithCustomBaseUrl_KeepsUserUrlAndSkipsProxyOverride()
    {
        var (template, provider) = await SeedAsync();
        // ToolSlugDefaults returns empty for OpenCode + ApiBaseUrl set,
        // so the proxy token never gets minted. The orchestrator falls
        // through to the credential / direct path, which keeps the
        // user-supplied URL.
        var proxy = StubMintingToken("must.not.be.minted");
        var service = MakeService(proxy);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "opencode-ollama",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-1",
            CodeAssistant = new CodeAssistantConfig
            {
                Tool = CodeAssistantType.OpenCode,
                ApiBaseUrl = "http://host.docker.internal:11434",
            },
        }, CancellationToken.None);

        var env = (await ReadEnqueuedJobAsync()).EnvironmentVariables;
        env.Should().NotBeNull();
        env!["OPENAI_API_BASE"].Should().Be("http://host.docker.internal:11434",
            "Ollama path: the user-supplied URL must reach OPENAI_API_BASE (the credential-path env var), not be replaced by the proxy URL.");
        env.Should().NotContainKey("OPENAI_BASE_URL",
            "no proxy routing — must not synthesise the proxy-flavoured env var.");
        proxy.MintCallCount.Should().Be(0,
            "an explicit ApiBaseUrl signals 'bypass the proxy' — no token mint should happen.");
    }

    [Fact]
    public async Task CreateContainer_CodexCli_SetsOpenAIEnvVarsToProxy()
    {
        var (template, provider) = await SeedAsync();
        var proxy = StubMintingToken("codex.jwt");
        var service = MakeService(proxy);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "codex",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-1",
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.CodexCli },
        }, CancellationToken.None);

        var env = (await ReadEnqueuedJobAsync()).EnvironmentVariables;
        env!["OPENAI_API_KEY"].Should().Be("codex.jwt");
        env["OPENAI_BASE_URL"].Should().Be(
            "http://host.docker.internal:9100/models/openai/v1");
    }

    [Fact]
    public async Task CreateContainer_Aider_SetsApiBaseAtAiderSpelling()
    {
        var (template, provider) = await SeedAsync();
        var proxy = StubMintingToken("aider.jwt");
        var service = MakeService(proxy);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "aider",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-1",
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.Aider },
        }, CancellationToken.None);

        var env = (await ReadEnqueuedJobAsync()).EnvironmentVariables;
        env!["OPENAI_API_BASE"].Should().Be(
            "http://host.docker.internal:9100/models/openai/v1",
            "Aider reads OPENAI_API_BASE (not _BASE_URL) — the wrong spelling silently means Aider hits api.openai.com directly.");
    }

    [Fact]
    public async Task CreateContainer_WithProxyButNoConfiguredUrl_LeavesProxyEnvVarsUnset()
    {
        var (template, provider) = await SeedAsync();
        var proxy = StubMintingToken("claude.jwt");
        // No Proxy:ContainerFacingBaseUrl → routing helper has no URL
        // to build with → tool-specific env vars stay nil. The
        // ANDY_SERVICE_TOKEN still lands (existing path); the
        // tool-specific override needs both inputs.
        var service = MakeService(proxy, configureProxyUrl: false);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "claude-no-proxy-url",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            OwnerId = "user-1",
            CodeAssistant = new CodeAssistantConfig { Tool = CodeAssistantType.ClaudeCode },
        }, CancellationToken.None);

        var env = (await ReadEnqueuedJobAsync()).EnvironmentVariables ?? new Dictionary<string, string>();
        env.Should().NotContainKey("ANTHROPIC_BASE_URL",
            "without a configured proxy URL, the tool-specific override must not fire — a half-applied override would point the tool at the empty string and break it.");
        env["ANDY_SERVICE_TOKEN"].Should().Be("claude.jwt",
            "the per-container token still lands — it's useful for scripts that read it directly.");
    }

    // -----------------------------------------------------------------
    // Helpers (mirror the proxy-token test file's setup)
    // -----------------------------------------------------------------

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedAsync()
    {
        var template = new ContainerTemplate
        {
            Code = "per-tool-env-test",
            Name = "Per-tool env test",
            Version = "1.0.0",
            BaseImage = "ubuntu:24.04",
        };
        var provider = new InfrastructureProvider
        {
            Code = "env-provider",
            Name = "Provider",
            Type = ProviderType.Docker,
            IsEnabled = true,
        };
        _db.Templates.Add(template);
        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();
        return (template, provider);
    }

    private StubProxyTokenService StubMintingToken(string jwt)
    {
        return new StubProxyTokenService(new MintedProxyToken(
            Guid.NewGuid(),
            jwt,
            DateTimeOffset.UtcNow.AddHours(1)));
    }

    private ContainerOrchestrationService MakeService(
        IProxyTokenService proxyTokens,
        bool configureProxyUrl = true)
    {
        var configValues = new Dictionary<string, string?>();
        if (configureProxyUrl)
        {
            configValues["Proxy:ContainerFacingBaseUrl"] = ProxyBase;
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
        return new ContainerOrchestrationService(
            _db,
            _mockRouting.Object,
            _mockFactory.Object,
            _queue,
            _mockProbe.Object,
            config,
            NullLogger<ContainerOrchestrationService>.Instance,
            serviceTokenService: new StubServiceTokenService("shared-m2m"),
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
    }

    private sealed class StubProxyTokenService : IProxyTokenService
    {
        private readonly MintedProxyToken? _mintResult;
        public int MintCallCount { get; private set; }

        public StubProxyTokenService(MintedProxyToken? mintResult)
        {
            _mintResult = mintResult;
        }

        public Task<MintedProxyToken?> MintForContainerAsync(
            string containerId,
            string subjectId,
            IReadOnlyList<string> allowedSlugs,
            CancellationToken ct = default)
        {
            MintCallCount++;
            return Task.FromResult(_mintResult);
        }

        public Task RevokeAsync(Guid tokenId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
