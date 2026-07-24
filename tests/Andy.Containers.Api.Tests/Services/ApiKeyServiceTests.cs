using System.Net;
using System.Text;
using System.Text.Json;
using Andy.Containers.Api.Controllers;
using Andy.Containers.Api.Services;
using Andy.Containers.Api.Tests.Helpers;
using Andy.Containers.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

public sealed class ApiKeyServiceTests : IDisposable
{
    private readonly ContainersDbContext _db = InMemoryDbHelper.CreateContext();
    private readonly InMemorySecretStore _secrets = new();
    private readonly StubValidator _validator = new();
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests()
        => _service = new ApiKeyService(_db, _secrets, _validator);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_StoresPlaintextOnlyInAndySettingsAndReturnsMaskedMetadata()
    {
        const string raw = "sk-test-12345678abcd";

        var entry = await _service.CreateAsync(
            "user-1",
            new CreateApiKeyCommand(
                "My OpenAI Key",
                "openai",
                raw,
                "gpt-4o",
                null));

        entry.Provider.Should().Be("openai");
        entry.MaskedValue.Should().Be("sk-••••••••abcd");
        entry.Model.Should().Be("gpt-4o");
        entry.IsValid.Should().BeNull();
        _secrets.Values[(
            "andy.models.providers.openai.apiKey",
            "user-1")].Should().Be(raw);

        var persisted = await _db.ApiKeyRegistrations.SingleAsync();
        persisted.MaskedValue.Should().Be("sk-••••••••abcd");
        typeof(Andy.Containers.Models.ApiKeyRegistration)
            .GetProperties()
            .Select(p => p.Name)
            .Should()
            .NotContain(name =>
                name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase) ||
                name == "Value" ||
                name == "PlaintextValue",
                "raw provider keys belong only to andy-settings");
        (await _db.ApiKeyAuditRecords.SingleAsync()).Kind.Should().Be("created");
    }

    [Fact]
    public async Task CrudValidateAndHistory_AreUserScopedAndRetainDeletionAudit()
    {
        var entry = await _service.CreateAsync(
            "owner",
            new CreateApiKeyCommand(
                "Primary",
                "anthropic",
                "sk-ant-old-value",
                null,
                null));

        var updated = await _service.UpdateAsync(
            entry.Id,
            "owner",
            new UpdateApiKeyCommand(
                "Rotated",
                "sk-ant-new-value",
                "claude-sonnet-4-7",
                null));
        updated.Name.Should().Be("Rotated");
        updated.MaskedValue.Should().EndWith("alue");
        updated.IsValid.Should().BeNull();

        _validator.Outcome = new ApiKeyValidationOutcome(
            true,
            "Key is functional.",
            42);
        var validation = await _service.ValidateAsync(entry.Id, "owner");
        validation.IsValid.Should().BeTrue();
        validation.QuotaRemaining.Should().Be(42);

        await _service.DeleteAsync(entry.Id, "owner");

        (await _service.ListAsync("owner")).Should().BeEmpty();
        _secrets.Values.Should().NotContainKey((
            "andy.models.providers.anthropic.apiKey",
            "owner"));
        var history = await _service.HistoryAsync(entry.Id, "owner");
        history.Select(h => h.Kind).Should().Equal(
            "deleted",
            "validated",
            "updated",
            "created");
        history.Should().OnlyContain(h => h.OwnerId == "owner");
    }

    [Fact]
    public async Task UserIsolation_HidesRegistrationAndHistory()
    {
        var entry = await _service.CreateAsync(
            "owner",
            new CreateApiKeyCommand("Key", "google", "secret-value", null, null));

        (await _service.ListAsync("another-user")).Should().BeEmpty();
        var act = () => _service.HistoryAsync(entry.Id, "another-user");
        await act.Should().ThrowAsync<ApiKeyNotFoundException>();
    }

    [Fact]
    public async Task Create_RejectsSecondKeyForSameProviderAndOwner()
    {
        await _service.CreateAsync(
            "owner",
            new CreateApiKeyCommand("First", "openai", "secret-one", null, null));

        var act = () => _service.CreateAsync(
            "owner",
            new CreateApiKeyCommand("Second", "openai", "secret-two", null, null));

        await act.Should().ThrowAsync<ApiKeyConflictException>();
        _secrets.Values[(
            "andy.models.providers.openai.apiKey",
            "owner")].Should().Be("secret-one");
    }

    [Fact]
    public async Task CustomProvider_RequiresValidBaseUrlBeforeWritingSecret()
    {
        var act = () => _service.CreateAsync(
            "owner",
            new CreateApiKeyCommand("Custom", "custom", "secret", null, null));

        await act.Should().ThrowAsync<ApiKeyValidationException>()
            .WithMessage("*BaseURL is required*");
        _secrets.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData("sk-1234567890abcd", "sk-••••••••abcd")]
    [InlineData("gsk_1234567890wxyz", "gsk_••••••••wxyz")]
    [InlineData("tiny", "••••••••tiny")]
    [InlineData("x", "••••••••x")]
    public void Mask_MatchesConductorWireHelper(string raw, string expected)
        => ApiKeyService.Mask(raw).Should().Be(expected);

    private sealed class InMemorySecretStore : IApiKeySecretStore
    {
        public Dictionary<(string Definition, string Owner), string> Values { get; } = new();

        public Task SetAsync(
            string definitionKey,
            string ownerId,
            string value,
            CancellationToken ct = default)
        {
            Values[(definitionKey, ownerId)] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(
            string definitionKey,
            string ownerId,
            CancellationToken ct = default)
        {
            Values.TryGetValue((definitionKey, ownerId), out var value);
            return Task.FromResult<string?>(value);
        }

        public Task ClearAsync(
            string definitionKey,
            string ownerId,
            CancellationToken ct = default)
        {
            Values.Remove((definitionKey, ownerId));
            return Task.CompletedTask;
        }
    }

    private sealed class StubValidator : IApiKeyValidator
    {
        public ApiKeyValidationOutcome Outcome { get; set; } =
            new(true, "Valid.", null);

        public Task<ApiKeyValidationOutcome> ValidateAsync(
            ApiKeyProviderDefinition provider,
            string value,
            string? baseUrl,
            CancellationToken ct = default)
            => Task.FromResult(Outcome);
    }
}

public sealed class ApiKeySettingsProxyTests
{
    [Fact]
    public async Task Store_WritesAndReadsUserScopedSecretWithoutLeakingIntoUri()
    {
        var requests = new List<(HttpMethod Method, string Uri, string? Body)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();
            requests.Add((
                request.Method,
                request.RequestUri!.ToString(),
                body));

            return request.Method == HttpMethod.Get
                ? JsonResponse(
                    """{"definitionKey":"andy.models.providers.openai.apiKey","value":"sk-secret"}""")
                : new HttpResponseMessage(HttpStatusCode.Created);
        });
        var store = CreateStore(handler);

        await store.SetAsync(
            "andy.models.providers.openai.apiKey",
            "user/with spaces",
            "sk-secret");
        var value = await store.GetAsync(
            "andy.models.providers.openai.apiKey",
            "user/with spaces");
        await store.ClearAsync(
            "andy.models.providers.openai.apiKey",
            "user/with spaces");

        value.Should().Be("sk-secret");
        requests[0].Body.Should().Contain("\"scopeType\":\"User\"");
        requests[0].Body.Should().Contain("\"scopeId\":\"user/with spaces\"");
        requests[0].Body.Should().Contain("\"value\":\"sk-secret\"");
        requests[1].Uri.Should().Contain("scopeType=User");
        requests[1].Uri.Should().Contain("scopeId=user%2Fwith spaces");
        requests[1].Uri.Should().NotContain("sk-secret");
        requests[2].Body.Should().Contain("\"value\":\"\"");
    }

    [Fact]
    public async Task Store_MapsSettingsOutageToUnavailable()
    {
        var store = CreateStore(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));

        var act = () => store.SetAsync(
            "andy.models.providers.openai.apiKey",
            "owner",
            "sk-MUST-NOT-LEAK-1234");

        await act.Should().ThrowAsync<ApiKeySecretStoreUnavailableException>()
            .Where(ex => !ex.Message.Contains(
                "sk-MUST-NOT-LEAK-1234",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validator_UsesHeaderAuthenticationAndNeverPlacesKeyInUrl()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation(
                "x-ratelimit-remaining-requests",
                "17");
            return Task.FromResult(response);
        });
        var validator = CreateValidator(handler);
        ApiKeyProviderCatalog.TryGet("openai", out var provider).Should().BeTrue();

        var result = await validator.ValidateAsync(
            provider,
            "sk-never-in-uri",
            null);

        result.Should().Be(new ApiKeyValidationOutcome(
            true,
            "Key is functional.",
            17));
        captured!.RequestUri!.ToString().Should().Be(
            "https://api.openai.com/v1/models");
        captured.RequestUri.ToString().Should().NotContain("sk-never-in-uri");
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("sk-never-in-uri");
    }

    [Fact]
    public async Task Validator_RejectsCredentialBearingBaseUrlBeforeNetwork()
    {
        var validator = CreateValidator(new StubHandler(_ =>
            throw new InvalidOperationException("network should not run")));
        ApiKeyProviderCatalog.TryGet("custom", out var provider).Should().BeTrue();

        var act = () => validator.ValidateAsync(
            provider,
            "secret",
            "https://user:password@example.com/v1");

        await act.Should().ThrowAsync<ApiKeyValidationException>();
    }

    [Fact]
    public void ApiKeyEntry_UsesExactConductorCamelCaseWireNamesAndNoRawValue()
    {
        var dto = new ApiKeyEntry(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "Primary",
            "openai-compatible",
            "sk-••••••••abcd",
            true,
            DateTimeOffset.Parse("2026-07-24T12:00:00Z"),
            null,
            null,
            "model",
            "https://example.test/v1");
        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"maskedValue\"");
        json.Should().Contain("\"isValid\"");
        json.Should().Contain("\"lastUsedAt\"");
        json.Should().Contain("\"lastValidatedAt\"");
        json.Should().Contain("\"baseURL\"");
        json.Should().NotContain("\"value\"");
        json.Should().NotContain("secret");
    }

    private static AndySettingsApiKeySecretStore CreateStore(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://andy-settings.test/"),
        };
        return new AndySettingsApiKeySecretStore(new SingleClientFactory(http));
    }

    private static ApiKeyValidator CreateValidator(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new ApiKeyValidator(new SingleClientFactory(http));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _responder(request);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
