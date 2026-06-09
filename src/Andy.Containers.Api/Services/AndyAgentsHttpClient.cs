// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Containers.Configurator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andy.Containers.Api.Services;

/// <summary>
/// AX.1 (rivoli-ai/conductor#2088). Real HTTP client for the andy-agents
/// service. Replaces <see cref="StubAndyAgentsClient"/> as the default
/// <see cref="IAndyAgentsClient"/> whenever <c>AndyAgents:ApiBaseUrl</c> is
/// configured. andy-agents becomes the source of truth for an agent's
/// INSTRUCTIONS and MODEL.
///
/// <para>
/// Wire contract (consumed, never modified here):
/// <c>GET /api/agents/by-slug/{slug}</c> → <c>AgentDto</c> JSON, requiring an
/// M2M bearer minted for audience <c>urn:andy-agents-api</c> (permission
/// <c>andy-agents:agent:read</c>). The bearer is attached by the named
/// HttpClient's <c>ServiceBearerHandler</c> (wired in <c>Program.cs</c>) — this
/// client itself is auth-agnostic.
/// </para>
///
/// <para>
/// <strong>Tools are intentionally NOT sourced here.</strong> The in-container
/// code assistant ships with its own built-in tool surface; the resolved
/// <see cref="AgentSpec.Tools"/> is always EMPTY. The permission allow-list
/// that gates those built-ins is AX.3/AX.4, not this slice. (andy-agents'
/// <c>ToolIds</c> are ignored.)
/// </para>
///
/// <para>
/// <strong>Failure contract:</strong> a 404 (unknown slug) maps to
/// <c>null</c> so the caller surfaces a clean 404-equivalent; any other
/// non-success status, transport error, or unparseable body throws a clear
/// <see cref="AndyAgentsResolutionException"/> — run configuration must NOT
/// proceed with a half-resolved agent.
/// </para>
/// </summary>
public sealed class AndyAgentsHttpClient : IAndyAgentsClient
{
    /// <summary>Named HttpClient registered in DI; tests can override.</summary>
    public const string HttpClientName = "andy-agents";

    // Forced model wiring. The in-container assistant talks to Conductor's
    // embedded andy-models proxy, which speaks the OpenAI dialect and reads
    // its bearer from OPENAI_API_KEY (a per-container aud=urn:andy-models-api
    // proxy token). So regardless of what provider andy-agents records, the
    // resolved spec ALWAYS declares the OpenAI dialect + the OPENAI_API_KEY
    // ref. ("openai" is in HeadlessConfigBuilder's allow-list.)
    private const string ForcedProvider = "openai";
    private const string ForcedApiKeyRef = "env:OPENAI_API_KEY";

    // andy-agents uses ASP.NET's default camelCase web JSON options for its
    // response bodies. Pin a dedicated options object (with enum-as-string,
    // since AgentDto.Status is an enum) so the deserialiser is independent of
    // any process-wide defaults.
    private static readonly JsonSerializerOptions AgentsResponseJson =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AndyAgentsOptions _options;
    private readonly ILogger<AndyAgentsHttpClient> _logger;

    public AndyAgentsHttpClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AndyAgentsOptions> options,
        ILogger<AndyAgentsHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentSpec?> GetAgentAsync(
        string agentSlug, int? revision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentSlug))
        {
            return null;
        }

        var http = _httpClientFactory.CreateClient(HttpClientName);
        var path = $"api/agents/by-slug/{Uri.EscapeDataString(agentSlug)}";

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
            throw new AndyAgentsResolutionException(
                $"andy-agents resolve for slug '{agentSlug}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "andy-agents has no agent for slug {Slug} (404) — resolving to null.", agentSlug);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response.Content, ct).ConfigureAwait(false);
                throw new AndyAgentsResolutionException(
                    $"andy-agents resolve for slug '{agentSlug}' returned HTTP " +
                    $"{(int)response.StatusCode} ({response.StatusCode}). Body preview: {Truncate(body, 200)}");
            }

            AgentDtoWire? dto;
            try
            {
                dto = await response.Content
                    .ReadFromJsonAsync<AgentDtoWire>(AgentsResponseJson, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new AndyAgentsResolutionException(
                    $"andy-agents resolve for slug '{agentSlug}' succeeded with HTTP " +
                    $"{(int)response.StatusCode} but the response body was unparseable.", ex);
            }

            if (dto is null)
            {
                throw new AndyAgentsResolutionException(
                    $"andy-agents resolve for slug '{agentSlug}' returned an empty body.");
            }

            return MapToAgentSpec(dto, revision, _options);
        }
    }

    /// <summary>
    /// Pure, side-effect-free mapping from the andy-agents <see cref="AgentDtoWire"/>
    /// to the configurator's <see cref="AgentSpec"/>. Extracted so the mapping
    /// rules (forced provider/key, model-id derivation, empty tools, limits) are
    /// unit-testable without an HTTP round-trip.
    ///
    /// <para>Mapping decisions:</para>
    /// <list type="bullet">
    /// <item><c>Slug</c> ← <c>Name</c>; <c>Revision</c> ← the caller's pin
    /// (andy-agents does not version specs over this endpoint yet).</item>
    /// <item><c>Instructions</c> ← <c>SystemPrompt</c>; returns <c>null</c> when
    /// the prompt is empty/whitespace — an instruction-less agent is not
    /// runnable, so the caller treats null as "no usable agent".</item>
    /// <item><c>Model.Provider</c> is FORCED to <c>openai</c> and
    /// <c>Model.ApiKeyRef</c> to <c>env:OPENAI_API_KEY</c> (the proxy dialect /
    /// per-container key), independent of andy-agents' recorded provider.</item>
    /// <item><c>Model.Id</c> is the model the proxy knows: from
    /// <c>ModelPreferences.Preferences[0].Slug</c> take the segment after the
    /// last '/' if present ("deepseek/deepseek-v4-flash" → "deepseek-v4-flash"),
    /// else the whole slug; falling back to <c>ModelName</c> when there is no
    /// usable pinned preference.</item>
    /// <item><c>Tools</c> is EMPTY (built-ins live in the assistant; AX.3/AX.4
    /// own the permission allow-list).</item>
    /// <item><c>Boundaries</c> null; <c>Limits</c> from the options-configurable
    /// defaults (per-agent limits are a later slice).</item>
    /// </list>
    /// </summary>
    /// <returns>
    /// The mapped spec, or <c>null</c> when <see cref="AgentDtoWire.SystemPrompt"/>
    /// is empty (no usable instructions).
    /// </returns>
    public static AgentSpec? MapToAgentSpec(AgentDtoWire dto, int? revision, AndyAgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(dto.SystemPrompt))
        {
            // No instructions → not a runnable agent. Treat as "no agent" so the
            // caller (RunConfigurator) maps it to a 404-equivalent rather than
            // dispatching a prompt-less run.
            return null;
        }

        var slug = string.IsNullOrWhiteSpace(dto.Name) ? "agent" : dto.Name;
        var modelId = DeriveModelId(dto);

        return new AgentSpec
        {
            Slug = slug,
            Revision = revision,
            Instructions = dto.SystemPrompt,
            OutputFormat = null,
            Model = new AgentSpecModel
            {
                Provider = ForcedProvider,
                Id = modelId,
                ApiKeyRef = ForcedApiKeyRef,
            },
            Tools = Array.Empty<AgentSpecTool>(),
            EnvVars = null,
            Boundaries = null,
            Limits = new AgentSpecLimits
            {
                MaxIterations = options.DefaultMaxIterations,
                TimeoutSeconds = options.DefaultTimeoutSeconds,
            },
        };
    }

    /// <summary>
    /// Resolves the model id the andy-models proxy knows. Preference order:
    /// the first pinned preference slug, then <see cref="AgentDtoWire.ModelName"/>.
    /// The FULL slug is used as-is: the andy-models registry keys on the whole
    /// slug INCLUDING the provider segment (e.g. "openrouter/qwen3-coder",
    /// "anthropic/claude-sonnet-4-6"), so stripping the prefix would produce an
    /// id the proxy can't resolve. (An unprefixed slug like "gpt-4o" passes
    /// through unchanged.)
    /// </summary>
    public static string DeriveModelId(AgentDtoWire dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var pinnedSlug = dto.ModelPreferences?.Preferences?
            .Select(p => p?.Slug)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        var raw = !string.IsNullOrWhiteSpace(pinnedSlug) ? pinnedSlug! : dto.ModelName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Last-resort: andy-agents always carries a ModelName, but be
            // defensive so a malformed DTO never produces an empty model id.
            return "default";
        }

        return raw.Trim();
    }

    private static async Task<string> SafeReadAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            return await content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "...";
    }

    /// <summary>
    /// Local read-only view of andy-agents' <c>AgentDto</c>
    /// (<c>GET /api/agents/by-slug/{slug}</c>). Only the fields AX.1 consumes
    /// are modelled — instructions (<see cref="SystemPrompt"/>), model
    /// (<see cref="ModelName"/> + <see cref="ModelPreferences"/>) and the
    /// echoed <see cref="Name"/>. The rest of the wire shape (id, providerId,
    /// temperature, maxTokens, status, toolIds, skills, allowedEnvironments, …)
    /// is present on the wire but deliberately unmodelled so the local record
    /// stays minimal and decoupled from andy-agents' build graph. The contract
    /// is duplicated, not referenced — the two services share no code.
    /// </summary>
    public sealed record AgentDtoWire(
        string? Name,
        string ModelName,
        string? SystemPrompt,
        AgentModelPreferencesWire? ModelPreferences);

    /// <summary>Subset of andy-agents' <c>AgentModelPreferences</c>.</summary>
    public sealed record AgentModelPreferencesWire(
        IReadOnlyList<AgentModelPreferenceWire?>? Preferences);

    /// <summary>Subset of andy-agents' <c>AgentModelPreference</c> — only the
    /// pinned <see cref="Slug"/> is consumed; recommendation hints are ignored
    /// (the proxy resolves a concrete id, not a hint).</summary>
    public sealed record AgentModelPreferenceWire(string? Slug);
}

/// <summary>
/// Thrown when resolving an agent from andy-agents fails for any reason other
/// than a clean 404 (unknown slug → null). Carries enough context for triage
/// without leaking response internals.
/// </summary>
public sealed class AndyAgentsResolutionException : Exception
{
    public AndyAgentsResolutionException(string message) : base(message) { }
    public AndyAgentsResolutionException(string message, Exception inner) : base(message, inner) { }
}
