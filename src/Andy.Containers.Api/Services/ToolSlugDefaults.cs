using Andy.Containers.Models;

namespace Andy.Containers.Api.Services;

/// <summary>
/// rivoli-ai/conductor#943 (M1.5.1). Default mapping from a
/// <see cref="CodeAssistantType"/> to the model slugs a per-container
/// proxy token should grant access to.
///
/// Used when <see cref="CodeAssistantConfig.RequiredModelSlugs"/> is
/// <c>null</c> — callers can override the default by setting that
/// field explicitly. An empty result means "no proxy token needed"
/// (the container talks to its model surface directly, e.g. Ollama or
/// an OpenAI-compatible self-hosted endpoint).
/// </summary>
public static class ToolSlugDefaults
{
    public static IReadOnlyList<string> Resolve(CodeAssistantConfig config)
    {
        if (config.RequiredModelSlugs is not null)
        {
            // Explicit override wins, including an explicit empty list
            // which signals "this container handles its own auth".
            return config.RequiredModelSlugs;
        }

        return config.Tool switch
        {
            // Claude Code's only sensible target is Anthropic-dialect;
            // pin to the current default Sonnet so the token is usable
            // out of the box. Users wanting Opus or a pinned earlier
            // version set RequiredModelSlugs explicitly.
            CodeAssistantType.ClaudeCode
                => new[] { "anthropic/claude-sonnet-4-6" },

            // OpenCode is multi-provider. Without an explicit slug
            // list we can't guess which provider the user actually
            // wants — return empty (no proxy token) and let the API
            // key resolution path handle credentials. Users routing
            // OpenCode through the proxy should set
            // RequiredModelSlugs (e.g. ["openai/gpt-4o"]).
            CodeAssistantType.OpenCode
                => Array.Empty<string>(),

            // Other tools — extend the map as concrete defaults
            // become clear. Returning empty keeps behaviour safe by
            // default (no proxy token rather than a wrongly-scoped one).
            _ => Array.Empty<string>(),
        };
    }
}
