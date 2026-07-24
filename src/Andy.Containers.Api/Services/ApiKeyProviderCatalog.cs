using System.Net;
using System.Net.Http.Headers;

namespace Andy.Containers.Api.Services;

public static class ApiKeyProviderCatalog
{
    private static readonly IReadOnlyDictionary<string, ApiKeyProviderDefinition> Providers =
        new Dictionary<string, ApiKeyProviderDefinition>(StringComparer.Ordinal)
        {
            ["anthropic"] = new(
                "anthropic",
                "andy.models.providers.anthropic.apiKey",
                new Uri("https://api.anthropic.com/v1/"),
                "models",
                ApiKeyAuthentication.Anthropic),
            ["openai"] = new(
                "openai",
                "andy.models.providers.openai.apiKey",
                new Uri("https://api.openai.com/v1/"),
                "models",
                ApiKeyAuthentication.Bearer),
            ["google"] = new(
                "google",
                "andy.models.providers.google.apiKey",
                new Uri("https://generativelanguage.googleapis.com/v1beta/"),
                "models",
                ApiKeyAuthentication.Google),
            ["dashscope"] = new(
                "dashscope",
                "andy.models.providers.alibaba.apiKey",
                new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1/"),
                "models",
                ApiKeyAuthentication.Bearer),
            ["openrouter"] = new(
                "openrouter",
                "andy.models.providers.openrouter.apiKey",
                new Uri("https://openrouter.ai/api/v1/"),
                "models",
                ApiKeyAuthentication.Bearer),
            ["ollama"] = new(
                "ollama",
                "andy.models.providers.ollama.apiKey",
                new Uri("http://localhost:11434/"),
                "api/tags",
                ApiKeyAuthentication.None),
            ["openai-compatible"] = new(
                "openai-compatible",
                "andy.models.providers.openai-compatible.apiKey",
                null,
                "models",
                ApiKeyAuthentication.Bearer),
            ["custom"] = new(
                "custom",
                "andy.models.providers.openai-compatible-generic.apiKey",
                null,
                "models",
                ApiKeyAuthentication.Bearer),
        };

    public static bool TryGet(
        string? provider,
        out ApiKeyProviderDefinition definition)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return Providers.TryGetValue(normalized ?? string.Empty, out definition!);
    }

    public static IReadOnlyCollection<string> SupportedProviders => Providers.Keys.ToArray();
}

public sealed record ApiKeyProviderDefinition(
    string WireName,
    string SecretDefinitionKey,
    Uri? DefaultBaseUri,
    string ValidationPath,
    ApiKeyAuthentication Authentication);

public enum ApiKeyAuthentication
{
    Bearer,
    Anthropic,
    Google,
    None,
}

public interface IApiKeyValidator
{
    Task<ApiKeyValidationOutcome> ValidateAsync(
        ApiKeyProviderDefinition provider,
        string value,
        string? baseUrl,
        CancellationToken ct = default);
}

public sealed record ApiKeyValidationOutcome(
    bool IsValid,
    string? Message,
    int? QuotaRemaining);

public sealed class ApiKeyValidator : IApiKeyValidator
{
    public const string HttpClientName = "api-key-validation";

    private readonly IHttpClientFactory _httpClientFactory;

    public ApiKeyValidator(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public async Task<ApiKeyValidationOutcome> ValidateAsync(
        ApiKeyProviderDefinition provider,
        string value,
        string? baseUrl,
        CancellationToken ct = default)
    {
        var endpoint = BuildValidationUri(provider, baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyAuthentication(request, provider.Authentication, value);

        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.IsSuccessStatusCode)
            {
                return new ApiKeyValidationOutcome(
                    true,
                    "Key is functional.",
                    ReadQuota(response.Headers));
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ApiKeyValidationOutcome(
                    false,
                    "Provider rejected the API key.",
                    null);
            }

            return new ApiKeyValidationOutcome(
                false,
                $"Provider validation returned HTTP {(int)response.StatusCode}.",
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ApiKeyValidationOutcome(
                false,
                "Provider validation could not be completed.",
                null);
        }
    }

    public static Uri BuildValidationUri(
        ApiKeyProviderDefinition provider,
        string? baseUrl)
    {
        Uri? baseUri;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUri = provider.DefaultBaseUri;
        }
        else if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out baseUri) ||
                 baseUri.Scheme is not ("http" or "https") ||
                 !string.IsNullOrEmpty(baseUri.UserInfo) ||
                 !string.IsNullOrEmpty(baseUri.Query) ||
                 !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ApiKeyValidationException(
                "BaseURL must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }

        if (baseUri is null)
        {
            throw new ApiKeyValidationException(
                $"BaseURL is required for provider '{provider.WireName}'.");
        }

        var normalized = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        return new Uri(normalized, provider.ValidationPath);
    }

    private static void ApplyAuthentication(
        HttpRequestMessage request,
        ApiKeyAuthentication authentication,
        string value)
    {
        switch (authentication)
        {
            case ApiKeyAuthentication.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
                break;
            case ApiKeyAuthentication.Anthropic:
                request.Headers.TryAddWithoutValidation("x-api-key", value);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            case ApiKeyAuthentication.Google:
                request.Headers.TryAddWithoutValidation("x-goog-api-key", value);
                break;
            case ApiKeyAuthentication.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(authentication),
                    authentication,
                    null);
        }
    }

    private static int? ReadQuota(HttpResponseHeaders headers)
    {
        foreach (var name in new[]
                 {
                     "x-ratelimit-remaining-requests",
                     "x-ratelimit-remaining",
                 })
        {
            if (headers.TryGetValues(name, out var values) &&
                int.TryParse(values.FirstOrDefault(), out var remaining))
            {
                return remaining;
            }
        }
        return null;
    }
}

public sealed class ApiKeyValidationException : Exception
{
    public ApiKeyValidationException(string message) : base(message) { }
}
