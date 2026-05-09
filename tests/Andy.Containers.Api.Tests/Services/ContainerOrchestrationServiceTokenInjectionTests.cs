using Andy.Containers.Abstractions;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// #944 / M1.5.2. Coverage of `ANDY_SERVICE_TOKEN` + `ANDY_PROXY_BASE_URL`
/// env-var injection performed by
/// <see cref="ContainerOrchestrationService.CreateContainerAsync"/>.
/// Asserts on the queued <see cref="ContainerProvisionJob"/>'s
/// `EnvironmentVariables` because that's what the worker hands to
/// the infrastructure provider's `CreateContainerAsync`.
/// </summary>
public class ContainerOrchestrationServiceTokenInjectionTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IInfrastructureRoutingService> _mockRouting = new();
    private readonly Mock<IInfrastructureProviderFactory> _mockFactory = new();
    private readonly Mock<IInfrastructureProvider> _mockInfra = new();
    private readonly ContainerProvisioningQueue _queue = new();
    private readonly Mock<IGitRepositoryProbeService> _mockProbe = new();

    public ContainerOrchestrationServiceTokenInjectionTests()
    {
        _db = InMemoryDbHelper.CreateContext();
        _mockFactory.Setup(f => f.GetProvider(It.IsAny<InfrastructureProvider>()))
            .Returns(_mockInfra.Object);
        _mockProbe.Setup(p => p.ProbeRepositoriesAsync(It.IsAny<IReadOnlyList<GitRepositoryConfig>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
    }

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // Happy paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateContainer_WhenProxyUrlAndTokenServiceConfigured_InjectsBothEnvVars()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Proxy:ContainerFacingBaseUrl"] = "http://host.docker.internal:9100",
        });
        var tokenService = new StubTokenService("test-jwt-bearer");
        var service = MakeService(config, tokenService);

        var request = new CreateContainerRequest
        {
            Name = "with-andy-env",
            TemplateId = template.Id,
            ProviderId = provider.Id,
        };
        await service.CreateContainerAsync(request, CancellationToken.None);

        var job = await ReadEnqueuedJobAsync();
        var env = job.EnvironmentVariables;
        env.Should().NotBeNull();
        env!["ANDY_PROXY_BASE_URL"].Should().Be("http://host.docker.internal:9100");
        env["ANDY_SERVICE_TOKEN"].Should().Be("test-jwt-bearer");
    }

    [Fact]
    public async Task CreateContainer_NoProxyUrl_DoesNotInjectAndyProxyBaseUrl()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var config = BuildConfig(new Dictionary<string, string?>());
        // Token still injects (independent of Proxy URL).
        var tokenService = new StubTokenService("token-only");
        var service = MakeService(config, tokenService);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "no-proxy",
            TemplateId = template.Id,
            ProviderId = provider.Id,
        }, CancellationToken.None);

        var job = await ReadEnqueuedJobAsync();
        job.EnvironmentVariables.Should().NotBeNull();
        job.EnvironmentVariables.Should().NotContainKey("ANDY_PROXY_BASE_URL");
        job.EnvironmentVariables!["ANDY_SERVICE_TOKEN"].Should().Be("token-only");
    }

    [Fact]
    public async Task CreateContainer_NoTokenService_StillSetsProxyUrl()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Proxy:ContainerFacingBaseUrl"] = "http://host.docker.internal:9100",
        });
        // No token service registered.
        var service = MakeService(config, tokenService: null);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "no-token-service",
            TemplateId = template.Id,
            ProviderId = provider.Id,
        }, CancellationToken.None);

        var job = await ReadEnqueuedJobAsync();
        job.EnvironmentVariables.Should().NotBeNull();
        job.EnvironmentVariables!["ANDY_PROXY_BASE_URL"].Should().Be("http://host.docker.internal:9100");
        job.EnvironmentVariables.Should().NotContainKey("ANDY_SERVICE_TOKEN");
    }

    // -----------------------------------------------------------------
    // Failure path: token mint throws
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateContainer_TokenMintFails_ContainerStillCreatedWithoutToken()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Proxy:ContainerFacingBaseUrl"] = "http://host.docker.internal:9100",
        });
        var tokenService = new StubTokenService(throwException: new ServiceTokenException("auth unreachable"));
        var service = MakeService(config, tokenService);

        // The container creation must NOT fail just because the
        // token endpoint is down — that would be a single point of
        // failure on every container creation.
        var container = await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "mint-fails",
            TemplateId = template.Id,
            ProviderId = provider.Id,
        }, CancellationToken.None);

        container.Status.Should().Be(ContainerStatus.Pending);

        var job = await ReadEnqueuedJobAsync();
        job.EnvironmentVariables.Should().NotBeNull();
        job.EnvironmentVariables!["ANDY_PROXY_BASE_URL"].Should().Be("http://host.docker.internal:9100");
        job.EnvironmentVariables.Should().NotContainKey("ANDY_SERVICE_TOKEN");
    }

    // -----------------------------------------------------------------
    // User-supplied env vars take precedence
    // -----------------------------------------------------------------

    [Fact]
    public async Task CreateContainer_UserSetAndyProxyBaseUrl_DoesNotOverride()
    {
        var (template, provider) = await SeedTemplateAndProvider();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Proxy:ContainerFacingBaseUrl"] = "http://default-proxy:9100",
        });
        var tokenService = new StubTokenService("default-token");
        var service = MakeService(config, tokenService);

        await service.CreateContainerAsync(new CreateContainerRequest
        {
            Name = "user-overrides",
            TemplateId = template.Id,
            ProviderId = provider.Id,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["ANDY_PROXY_BASE_URL"] = "http://user-supplied:1234",
                ["ANDY_SERVICE_TOKEN"] = "user-supplied-token",
            },
        }, CancellationToken.None);

        var job = await ReadEnqueuedJobAsync();
        job.EnvironmentVariables!["ANDY_PROXY_BASE_URL"].Should().Be("http://user-supplied:1234",
                "explicit user-supplied env vars must win — otherwise the user can't escape the default for testing.");
        job.EnvironmentVariables["ANDY_SERVICE_TOKEN"].Should().Be("user-supplied-token");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<(ContainerTemplate template, InfrastructureProvider provider)> SeedTemplateAndProvider()
    {
        var template = new ContainerTemplate
        {
            Code = "tok-test",
            Name = "Token Test",
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

    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private ContainerOrchestrationService MakeService(IConfiguration configuration, IServiceTokenService? tokenService)
    {
        return new ContainerOrchestrationService(
            _db,
            _mockRouting.Object,
            _mockFactory.Object,
            _queue,
            _mockProbe.Object,
            new Mock<IApiKeyService>().Object,
            configuration,
            NullLogger<ContainerOrchestrationService>.Instance,
            tokenService);
    }

    private async Task<ContainerProvisionJob> ReadEnqueuedJobAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await _queue.Reader.ReadAsync(cts.Token);
    }

    /// <summary>
    /// Minimal <see cref="IServiceTokenService"/> stub: returns a fixed
    /// token, or throws the supplied exception. Avoids dragging in
    /// Moq just for one method.
    /// </summary>
    private sealed class StubTokenService : IServiceTokenService
    {
        private readonly string? _token;
        private readonly Exception? _exception;

        public StubTokenService(string token) { _token = token; }
        public StubTokenService(Exception throwException) { _exception = throwException; }

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => _exception is not null
                ? Task.FromException<string>(_exception)
                : Task.FromResult(_token!);
    }
}
