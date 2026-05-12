using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#944 (M1.5.2). Maps a <see cref="CodeAssistantType"/>
/// to the env-var trio the assistant inside the container reads:
/// (1) the API-key env var the tool consults for the bearer to send,
/// (2) the base-URL env var the tool consults to pick its server, and
/// (3) the dialect path under the andy-models proxy that handles the
/// tool's wire format.
///
/// The orchestrator combines these with the proxy URL +
/// per-container service token to produce a concrete env dictionary
/// the runtime adapter (Docker / Apple Containers) injects at create
/// time.
///
/// Returning <c>null</c> means "the tool is not proxy-routed" —
/// either because it doesn't speak HTTP to an LLM (GitHub Copilot's
/// own auth, Continue's IDE-local config) or because the user
/// explicitly bypassed the proxy via <see cref="CodeAssistantConfig.ApiBaseUrl"/>
/// (Ollama path, OpenAI-compatible self-hosted backends).
/// </summary>
public static class CodeAssistantProxyRouting
{
    public sealed record EnvVars(string KeyEnvVar, string BaseUrlEnvVar, string DialectPath);

    /// <summary>
    /// Returns the proxy routing for the given assistant config, or
    /// <c>null</c> when the tool either doesn't speak to an LLM HTTP
    /// surface or the user supplied an explicit
    /// <see cref="CodeAssistantConfig.ApiBaseUrl"/> (which signals
    /// "don't proxy — talk directly to this URL").
    /// </summary>
    public static EnvVars? For(CodeAssistantConfig config)
    {
        // Explicit user-supplied base URL always wins. For OpenCode this
        // means "user picked OpenAI-compatible (custom URL) or Ollama"
        // in the launch UI's sub-picker (M1.6.2 / conductor#948). The
        // orchestrator's existing ApiBaseUrl injection path handles
        // those cases; the per-tool proxy routing here is the "default
        // proxy mode" path that fires when the user accepts the
        // built-in routing.
        if (!string.IsNullOrWhiteSpace(config.ApiBaseUrl))
        {
            return null;
        }

        return config.Tool switch
        {
            // Claude Code reads ANTHROPIC_BASE_URL + ANTHROPIC_API_KEY
            // out of the box. Route both at the proxy's anthropic
            // dialect.
            CodeAssistantType.ClaudeCode
                => new EnvVars("ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "anthropic/v1"),

            // OpenCode + Codex CLI both speak the OpenAI Chat Completions
            // dialect; the install scripts set OPENAI_BASE_URL +
            // OPENAI_API_KEY.
            CodeAssistantType.OpenCode
                => new EnvVars("OPENAI_API_KEY", "OPENAI_BASE_URL", "openai/v1"),
            CodeAssistantType.CodexCli
                => new EnvVars("OPENAI_API_KEY", "OPENAI_BASE_URL", "openai/v1"),

            // Aider historically read OPENAI_API_BASE (not _BASE_URL).
            // Both have been valid for a while but the older form is
            // safer for Aider's docs / scripts.
            CodeAssistantType.Aider
                => new EnvVars("OPENAI_API_KEY", "OPENAI_API_BASE", "openai/v1"),

            // QwenCoder + GeminiCode + their cousins: routing TBD; the
            // andy-models proxy needs an alibaba / google dialect mount
            // before we can flip them on. Returning null keeps the
            // legacy credential path running for now.
            CodeAssistantType.QwenCoder => null,
            CodeAssistantType.GeminiCode => null,

            // IDE-local + provider-bring-your-own-auth tools don't take
            // an HTTP bearer at all.
            CodeAssistantType.Continue => null,
            CodeAssistantType.GitHubCopilot => null,
            CodeAssistantType.AmazonQ => null,
            CodeAssistantType.Cline => null,

            _ => null,
        };
    }

    /// <summary>
    /// Build the full container-facing URL for a proxy dialect. Joins
    /// the container-facing proxy base (e.g.
    /// <c>http://host.docker.internal:9100</c>) with the
    /// <c>/models/&lt;dialect&gt;</c> mount that <c>andy-models</c>
    /// exposes plus the dialect's <c>/v1</c> path.
    /// </summary>
    public static string BuildBaseUrl(string proxyBaseUrl, string dialectPath)
    {
        var trimmed = proxyBaseUrl.TrimEnd('/');
        return $"{trimmed}/models/{dialectPath.TrimStart('/')}";
    }
}
