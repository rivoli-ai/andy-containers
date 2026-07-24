using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Andy.Containers.Api.Services;

/// <summary>
/// User-scoped provider-key storage. Implementations must never log or expose
/// plaintext values beyond the trusted service boundary.
/// </summary>
public interface IApiKeySecretStore
{
    Task SetAsync(
        string definitionKey,
        string ownerId,
        string value,
        CancellationToken ct = default);

    Task<string?> GetAsync(
        string definitionKey,
        string ownerId,
        CancellationToken ct = default);

    /// <summary>
    /// Revokes the scoped secret without deleting other users' or machine
    /// scopes. andy-settings' current DELETE route is definition-wide, so the
    /// safe scoped operation is rotation to an empty value.
    /// </summary>
    Task ClearAsync(
        string definitionKey,
        string ownerId,
        CancellationToken ct = default);
}

public sealed class AndySettingsApiKeySecretStore : IApiKeySecretStore
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AndySettingsApiKeySecretStore(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public Task SetAsync(
        string definitionKey,
        string ownerId,
        string value,
        CancellationToken ct = default)
        => WriteAsync(definitionKey, ownerId, value, ct);

    public Task ClearAsync(
        string definitionKey,
        string ownerId,
        CancellationToken ct = default)
        => WriteAsync(definitionKey, ownerId, string.Empty, ct);

    public async Task<string?> GetAsync(
        string definitionKey,
        string ownerId,
        CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient(AndySettingsHttpClient.HttpClientName);
        var path =
            $"api/secrets/{Uri.EscapeDataString(definitionKey)}" +
            $"?scopeType=User&scopeId={Uri.EscapeDataString(ownerId)}";

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(path, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiKeySecretStoreUnavailableException(
                "The API-key secret store is unavailable.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable(response.StatusCode);
            }

            try
            {
                var payload = await response.Content
                    .ReadFromJsonAsync<SecretValueResponse>(cancellationToken: ct);
                return string.IsNullOrWhiteSpace(payload?.Value) ? null : payload.Value;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new ApiKeySecretStoreUnavailableException(
                    "The API-key secret store returned an invalid response.", ex);
            }
        }
    }

    private async Task WriteAsync(
        string definitionKey,
        string ownerId,
        string value,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(AndySettingsHttpClient.HttpClientName);
        var path = $"api/secrets/{Uri.EscapeDataString(definitionKey)}";

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(
                path,
                new
                {
                    scopeType = "User",
                    scopeId = ownerId,
                    value,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiKeySecretStoreUnavailableException(
                "The API-key secret store is unavailable.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable(response.StatusCode);
            }
        }
    }

    private static ApiKeySecretStoreUnavailableException Unavailable(HttpStatusCode status)
        => new(
            "The API-key secret store rejected the request " +
            $"with HTTP {(int)status} ({status}).");

    private sealed record SecretValueResponse(string? DefinitionKey, string? Value);
}

public sealed class UnavailableApiKeySecretStore : IApiKeySecretStore
{
    public Task SetAsync(
        string definitionKey,
        string ownerId,
        string value,
        CancellationToken ct = default)
        => Task.FromException(Unavailable());

    public Task<string?> GetAsync(
        string definitionKey,
        string ownerId,
        CancellationToken ct = default)
        => Task.FromException<string?>(Unavailable());

    public Task ClearAsync(
        string definitionKey,
        string ownerId,
        CancellationToken ct = default)
        => Task.FromException(Unavailable());

    private static ApiKeySecretStoreUnavailableException Unavailable()
        => new(
            "API-key management requires AndySettings:ApiBaseUrl to be configured.");
}

public sealed class ApiKeySecretStoreUnavailableException : Exception
{
    public ApiKeySecretStoreUnavailableException(string message) : base(message) { }

    public ApiKeySecretStoreUnavailableException(string message, Exception inner)
        : base(message, inner) { }
}
