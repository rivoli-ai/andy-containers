using Andy.Containers.Api.Services;
using Andy.Containers.Infrastructure.Data;
using Andy.Containers.Models;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public class ApiKeyServiceTests : IDisposable
{
    private readonly ContainersDbContext _db;
    private readonly Mock<IApiKeyValidationService> _mockValidation;
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests()
    {
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ContainersDbContext(options);
        _mockValidation = new Mock<IApiKeyValidationService>();
        _mockValidation.Setup(v => v.ValidateAsync(It.IsAny<ApiKeyProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyValidationResult(true));

        var dataProtection = new EphemeralDataProtectionProvider();
        var mockInstallService = new Mock<ICodeAssistantInstallService>();
        var logger = new Mock<ILogger<ApiKeyService>>();

        _service = new ApiKeyService(_db, dataProtection, _mockValidation.Object, mockInstallService.Object, logger.Object);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_StoresEncryptedKey()
    {
        var key = await _service.CreateAsync("user1", "my-key", ApiKeyProvider.Anthropic, "sk-ant-test-key-1234");

        key.Should().NotBeNull();
        key.Label.Should().Be("my-key");
        key.Provider.Should().Be(ApiKeyProvider.Anthropic);
        key.EnvVarName.Should().Be("ANTHROPIC_API_KEY");
        key.EncryptedValue.Should().NotBe("sk-ant-test-key-1234");
        key.MaskedValue.Should().Be("****...1234");
        key.IsValid.Should().BeTrue();
        key.LastValidatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_ValidatesKeyOnCreation()
    {
        _mockValidation.Setup(v => v.ValidateAsync(ApiKeyProvider.OpenAI, "bad-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyValidationResult(false, "Invalid API key"));

        var key = await _service.CreateAsync("user1", "bad", ApiKeyProvider.OpenAI, "bad-key");

        key.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Create_RecordsChangeHistory()
    {
        var key = await _service.CreateAsync("user1", "test", ApiKeyProvider.Anthropic, "sk-test", ipAddress: "1.2.3.4");

        var history = await _service.GetHistoryAsync(key.Id, "user1");
        history.Should().HaveCount(1);
        history[0].Action.Should().Be("created");
        history[0].IpAddress.Should().Be("1.2.3.4");
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnKeys()
    {
        await _service.CreateAsync("user1", "key1", ApiKeyProvider.Anthropic, "sk-1");
        await _service.CreateAsync("user2", "key2", ApiKeyProvider.OpenAI, "sk-2");

        var keys = await _service.ListAsync("user1");
        keys.Should().HaveCount(1);
        keys[0].Label.Should().Be("key1");
    }

    [Fact]
    public async Task Get_ReturnsNullForOtherUser()
    {
        var key = await _service.CreateAsync("user1", "key1", ApiKeyProvider.Anthropic, "sk-1");

        var result = await _service.GetAsync(key.Id, "user2");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Update_ChangesLabelAndRevalidates()
    {
        var key = await _service.CreateAsync("user1", "old-label", ApiKeyProvider.Anthropic, "sk-1");

        var updated = await _service.UpdateAsync(key.Id, "user1", "new-label", "sk-new-key", "5.6.7.8");

        updated.Should().NotBeNull();
        updated!.Label.Should().Be("new-label");
        updated.MaskedValue.Should().Contain("key");

        var history = await _service.GetHistoryAsync(key.Id, "user1");
        history.Should().HaveCount(2);
        history[1].Action.Should().Be("updated");
    }

    [Fact]
    public async Task Delete_RemovesKey()
    {
        var key = await _service.CreateAsync("user1", "to-delete", ApiKeyProvider.Google, "goog-1");

        var deleted = await _service.DeleteAsync(key.Id, "user1");
        deleted.Should().BeTrue();

        var keys = await _service.ListAsync("user1");
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_ReturnsFalseForOtherUser()
    {
        var key = await _service.CreateAsync("user1", "key1", ApiKeyProvider.Anthropic, "sk-1");

        var deleted = await _service.DeleteAsync(key.Id, "user2");
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateExisting_UpdatesValidationStatus()
    {
        var key = await _service.CreateAsync("user1", "key1", ApiKeyProvider.Anthropic, "sk-1");

        _mockValidation.Setup(v => v.ValidateAsync(ApiKeyProvider.Anthropic, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyValidationResult(false, "Revoked"));

        var result = await _service.ValidateExistingAsync(key.Id, "user1", "10.0.0.1");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Revoked");

        var refreshed = await _service.GetAsync(key.Id, "user1");
        refreshed!.IsValid.Should().BeFalse();

        var history = await _service.GetHistoryAsync(key.Id, "user1");
        history.Last().Action.Should().Be("validated");
    }

    [Fact]
    public async Task ResolveKey_ReturnsDecryptedValueAndTracksUsage()
    {
        await _service.CreateAsync("user1", "key1", ApiKeyProvider.Anthropic, "sk-ant-secret-1234");

        var resolved = await _service.ResolveKeyAsync("user1", ApiKeyProvider.Anthropic);

        resolved.Should().Be("sk-ant-secret-1234");

        var keys = await _service.ListAsync("user1");
        keys[0].LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ResolveKey_ReturnsNullWhenNotFound()
    {
        var resolved = await _service.ResolveKeyAsync("user1", ApiKeyProvider.Google);
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ListByOrganization_ReturnsOrgScopedKeys()
    {
        var orgId = Guid.NewGuid();
        await _service.CreateAsync("user1", "org-key", ApiKeyProvider.OpenAI, "sk-1", organizationId: orgId);
        await _service.CreateAsync("user2", "personal-key", ApiKeyProvider.OpenAI, "sk-2");

        var orgKeys = await _service.ListByOrganizationAsync(orgId);
        orgKeys.Should().HaveCount(1);
        orgKeys[0].Label.Should().Be("org-key");
    }

    [Theory]
    [InlineData(ApiKeyProvider.Anthropic, "ANTHROPIC_API_KEY")]
    [InlineData(ApiKeyProvider.OpenAI, "OPENAI_API_KEY")]
    [InlineData(ApiKeyProvider.Google, "GOOGLE_API_KEY")]
    [InlineData(ApiKeyProvider.Dashscope, "DASHSCOPE_API_KEY")]
    public async Task Create_DefaultsEnvVarByProvider(ApiKeyProvider provider, string expectedEnvVar)
    {
        var key = await _service.CreateAsync("user1", $"key-{provider}", provider, "test-key");
        key.EnvVarName.Should().Be(expectedEnvVar);
    }

    [Fact]
    public async Task Create_UsesCustomEnvVarWhenProvided()
    {
        var key = await _service.CreateAsync("user1", "custom", ApiKeyProvider.Anthropic, "sk-1", envVarName: "MY_CUSTOM_KEY");
        key.EnvVarName.Should().Be("MY_CUSTOM_KEY");
    }

    [Fact]
    public async Task MaskedValue_ShortKeysShowOnlyStars()
    {
        var key = await _service.CreateAsync("user1", "short", ApiKeyProvider.Custom, "abc");
        key.MaskedValue.Should().Be("****");
    }
}
