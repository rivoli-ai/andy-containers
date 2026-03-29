using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Andy.Containers.Api.Telemetry;
using Andy.Containers.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Containers.Api.Services;

public class ApiKeyValidationService : IApiKeyValidationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApiKeyValidationService> _logger;

    public ApiKeyValidationService(IHttpClientFactory httpClientFactory, ILogger<ApiKeyValidationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(ApiKeyProvider provider, string apiKey, CancellationToken ct)
    {
        using var activity = ActivitySources.ApiKeys.StartActivity("ApiKey.Validate");
        activity?.SetTag("apiKey.provider", provider.ToString());
        var sw = Stopwatch.StartNew();

        try
        {
            var result = provider switch
            {
                ApiKeyProvider.Anthropic => await ValidateAnthropicAsync(apiKey, ct),
                ApiKeyProvider.OpenAI => await ValidateOpenAIAsync(apiKey, ct),
                ApiKeyProvider.Google => await ValidateGoogleAsync(apiKey, ct),
                ApiKeyProvider.Dashscope => await ValidateDashscopeAsync(apiKey, ct),
                ApiKeyProvider.Custom => new ApiKeyValidationResult(true),
                ApiKeyProvider.OpenRouter => await ValidateOpenRouterAsync(apiKey, ct),
                ApiKeyProvider.Ollama => new ApiKeyValidationResult(true),
                ApiKeyProvider.OpenAiCompatible => string.IsNullOrEmpty(apiKey)
                    ? new ApiKeyValidationResult(true)
                    : new ApiKeyValidationResult(true),
                _ => new ApiKeyValidationResult(false, $"Unknown provider: {provider}")
            };

            sw.Stop();
            Meters.ApiKeysValidated.Add(1, new KeyValuePair<string, object?>("provider", provider.ToString()),
                new KeyValuePair<string, object?>("result", result.IsValid ? "valid" : "invalid"));
            Meters.ApiKeyValidationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", provider.ToString()));

            activity?.SetTag("apiKey.isValid", result.IsValid);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API key validation failed for provider {Provider}", provider);
            sw.Stop();
            Meters.ApiKeyValidationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", provider.ToString()));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new ApiKeyValidationResult(false, $"Validation error: {ex.Message}");
        }
    }

    private async Task<ApiKeyValidationResult> ValidateAnthropicAsync(string apiKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { model = "claude-haiku-4-5-20251001", max_tokens = 1, messages = new[] { new { role = "user", content = "hi" } } }),
            Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return new ApiKeyValidationResult(false, "Invalid API key");

        return new ApiKeyValidationResult(true);
    }

    private async Task<ApiKeyValidationResult> ValidateOpenAIAsync(string apiKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return new ApiKeyValidationResult(false, "Invalid API key");

        return new ApiKeyValidationResult(true);
    }

    private async Task<ApiKeyValidationResult> ValidateGoogleAsync(string apiKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"https://generativelanguage.googleapis.com/v1/models?key={apiKey}", ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.BadRequest)
            return new ApiKeyValidationResult(false, "Invalid API key");

        return new ApiKeyValidationResult(true);
    }

    private async Task<ApiKeyValidationResult> ValidateDashscopeAsync(string apiKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/text-generation/generation");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { model = "qwen-turbo", input = new { messages = new[] { new { role = "user", content = "hi" } } } }),
            Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return new ApiKeyValidationResult(false, "Invalid API key");

        return new ApiKeyValidationResult(true);
    }

    private async Task<ApiKeyValidationResult> ValidateOpenRouterAsync(string apiKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return new ApiKeyValidationResult(false, "Invalid API key");

        return new ApiKeyValidationResult(true);
    }
}
