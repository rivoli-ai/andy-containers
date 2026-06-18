// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#2242. Reads the source-control GitHub PAT
/// (<c>sourceControl.github.pat</c>) from andy-settings so it can be injected
/// into a task container as a FALLBACK git credential when the user has
/// registered no per-host credential of their own.
///
/// <para>
/// Wire contract (consumed, never modified here):
/// <c>GET /api/secrets/{key}?scopeType=Machine</c> → <c>{ definitionKey, value }</c>
/// JSON, requiring an M2M bearer minted for audience <c>urn:andy-settings-api</c>.
/// The bearer is attached by the named HttpClient's <c>ServiceBearerHandler</c>
/// (wired in <c>Program.cs</c>); this client is auth-agnostic.
/// </para>
///
/// <para>
/// <strong>Failure contract:</strong> a 404 (no secret set) maps to
/// <c>null</c> — the caller surfaces a clean "no credential" path. Any other
/// non-success status / transport error / unparseable body throws so a
/// transient settings outage is NOT silently treated as "no PAT".
/// </para>
/// </summary>
public sealed class AndySettingsHttpClient : ISourceControlSecretResolver
{
    /// <summary>Named HttpClient registered in DI; tests can override.</summary>
    public const string HttpClientName = "andy-settings";

    private static readonly JsonSerializerOptions ResponseJson =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AndySettingsOptions _options;
    private readonly ILogger<AndySettingsHttpClient> _logger;

    public AndySettingsHttpClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AndySettingsOptions> options,
        ILogger<AndySettingsHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task<string?> GetGitHubPatAsync(CancellationToken ct = default)
        => GetSecretAsync(_options.GitHubPatKey, ct);

    private async Task<string?> GetSecretAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var http = _httpClientFactory.CreateClient(HttpClientName);
        var path = $"api/secrets/{Uri.EscapeDataString(key)}?scopeType=Machine";

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new AndySettingsResolutionException(
                $"andy-settings secret resolve for '{key}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "andy-settings has no secret for key {Key} (404) — resolving to null.", key);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AndySettingsResolutionException(
                    $"andy-settings secret resolve for '{key}' returned HTTP " +
                    $"{(int)response.StatusCode} ({response.StatusCode}).");
            }

            SecretValueResponse? payload;
            try
            {
                payload = await response.Content
                    .ReadFromJsonAsync<SecretValueResponse>(ResponseJson, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new AndySettingsResolutionException(
                    $"andy-settings secret resolve for '{key}' succeeded but the response " +
                    "body was unparseable.", ex);
            }

            var value = payload?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>Subset of andy-settings' secret-value response body.</summary>
    private sealed record SecretValueResponse(string? DefinitionKey, string? Value);
}

/// <summary>
/// Thrown when resolving a secret from andy-settings fails for any reason other
/// than a clean 404 (no secret set → null).
/// </summary>
public sealed class AndySettingsResolutionException : Exception
{
    public AndySettingsResolutionException(string message) : base(message) { }
    public AndySettingsResolutionException(string message, Exception inner) : base(message, inner) { }
}
