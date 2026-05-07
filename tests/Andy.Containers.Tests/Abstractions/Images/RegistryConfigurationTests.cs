using Andy.Containers.Abstractions.Images;
using Andy.Containers.Configuration;
using Andy.Containers.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Containers.Tests.Abstractions.Images;

// IM2 (rivoli-ai/andy-containers#251). The abstractions are pure
// contracts plus one default IRegistryConfiguration implementation
// reading from RegistryConfigurationOptions. These tests pin the
// behaviour callers depend on:
//   - AddImageManagement composes a working DI graph
//   - Primary resolution prefers explicit > IsPrimary > first
//   - GetByIdOrThrow surfaces the configured-ids list to help debugging
//   - Empty config is an explicit InvalidOperationException, not silent null
public class RegistryConfigurationTests
{
    [Fact]
    public void AddImageManagement_RegistersIRegistryConfigurationAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddImageManagement();

        // Resolution requires options to be configured even if empty.
        services.Configure<RegistryConfigurationOptions>(_ => { });

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IRegistryConfiguration>();
        var second = provider.GetRequiredService<IRegistryConfiguration>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AddImageManagement_BindsImageManagementSection_WhenConfigurationProvided()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["ImageManagement:PrimaryRegistryId"] = "registry-from-config",
            ["ImageManagement:Registries:0:Id"] = "registry-from-config",
            ["ImageManagement:Registries:0:Kind"] = "zot",
            ["ImageManagement:Registries:0:Url"] = "http://localhost:5050",
            ["ImageManagement:Registries:0:IsPrimary"] = "true",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
        var services = new ServiceCollection();

        services.AddImageManagement(configuration);

        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IRegistryConfiguration>();

        config.Registries.Should().HaveCount(1);
        config.Registries[0].Id.Should().Be("registry-from-config");
        config.Registries[0].Kind.Should().Be("zot");
        config.PrimaryRegistryId.Should().Be("registry-from-config");
    }

    [Fact]
    public void AddImageManagement_DoesNotOverrideExistingRegistration()
    {
        var services = new ServiceCollection();
        var customConfig = new StubRegistryConfiguration();
        services.AddSingleton<IRegistryConfiguration>(customConfig);

        services.AddImageManagement();
        services.Configure<RegistryConfigurationOptions>(_ => { });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IRegistryConfiguration>()
            .Should().BeSameAs(customConfig,
                "AddImageManagement uses TryAddSingleton so callers can override the default impl.");
    }

    [Fact]
    public void Registries_IsEmpty_WhenNoneConfigured()
    {
        var config = BuildConfig(_ => { });
        config.Registries.Should().BeEmpty();
    }

    [Fact]
    public void GetByIdOrThrow_Throws_WhenIdUnknown()
    {
        var config = BuildConfig(opts =>
        {
            opts.Registries.Add(KnownEntry("local-zot"));
        });

        var act = () => config.GetByIdOrThrow("not-configured");

        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*not-configured*")
            .Which.Message.Should().Contain("'local-zot'",
                "the error includes the list of configured ids so a typo is obvious.");
    }

    [Fact]
    public void GetByIdOrThrow_ReturnsEntry_WhenMatch()
    {
        var entry = KnownEntry("local-zot");
        var config = BuildConfig(opts => opts.Registries.Add(entry));

        config.GetByIdOrThrow("local-zot").Should().Be(entry);
    }

    [Fact]
    public void PrimaryRegistryId_Throws_WhenNoRegistriesConfigured()
    {
        var config = BuildConfig(_ => { });

        var act = () => config.PrimaryRegistryId;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No registries configured*");
    }

    [Fact]
    public void PrimaryRegistryId_PrefersExplicitOption()
    {
        var config = BuildConfig(opts =>
        {
            opts.Registries.Add(KnownEntry("alpha", isPrimary: true));
            opts.Registries.Add(KnownEntry("beta"));
            opts.PrimaryRegistryId = "beta";
        });

        config.PrimaryRegistryId.Should().Be("beta",
            "explicit PrimaryRegistryId beats the IsPrimary flag.");
    }

    [Fact]
    public void PrimaryRegistryId_ThrowsWhenExplicitOptionDoesNotMatchAnyEntry()
    {
        var config = BuildConfig(opts =>
        {
            opts.Registries.Add(KnownEntry("alpha"));
            opts.PrimaryRegistryId = "typo";
        });

        var act = () => config.PrimaryRegistryId;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'typo'*does not match*");
    }

    [Fact]
    public void PrimaryRegistryId_FallsBackToIsPrimaryFlag()
    {
        var config = BuildConfig(opts =>
        {
            opts.Registries.Add(KnownEntry("alpha"));
            opts.Registries.Add(KnownEntry("beta", isPrimary: true));
            opts.Registries.Add(KnownEntry("gamma"));
        });

        config.PrimaryRegistryId.Should().Be("beta");
    }

    [Fact]
    public void PrimaryRegistryId_FallsBackToFirstEntry_WhenNothingFlagged()
    {
        var config = BuildConfig(opts =>
        {
            opts.Registries.Add(KnownEntry("alpha"));
            opts.Registries.Add(KnownEntry("beta"));
        });

        config.PrimaryRegistryId.Should().Be("alpha");
    }

    private static IRegistryConfiguration BuildConfig(Action<RegistryConfigurationOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddImageManagement();
        services.Configure(configure);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IRegistryConfiguration>();
    }

    private static RegistryConfigEntry KnownEntry(string id, bool isPrimary = false)
        => new()
        {
            Id = id,
            Kind = "zot",
            Url = $"http://localhost:5050/{id}",
            IsPrimary = isPrimary,
        };

    private sealed class StubRegistryConfiguration : IRegistryConfiguration
    {
        public IReadOnlyList<RegistryConfigEntry> Registries => Array.Empty<RegistryConfigEntry>();
        public string PrimaryRegistryId => "stub";
        public RegistryConfigEntry GetByIdOrThrow(string registryId) => throw new KeyNotFoundException();
    }
}
