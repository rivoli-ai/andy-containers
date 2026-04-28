using System.CommandLine;

namespace Andy.Containers.Cli.Formatting;

/// <summary>
/// X7 (rivoli-ai/andy-containers#97). Shared <c>--format</c> contract for
/// per-service CLIs (Epic AN). Two values today; <c>yaml</c> follows
/// once a consumer needs it. Centralised so future commands (and an
/// AP9/X7 retrofit) reuse one parser instead of re-deriving it.
/// </summary>
public enum OutputFormat
{
    /// <summary>Spectre.Console table — the default human-readable view.</summary>
    Table,
    /// <summary>Newline-terminated JSON — script-friendly, one record per response.</summary>
    Json,
}

public static class OutputFormatOption
{
    /// <summary>
    /// Build a System.CommandLine option named <c>--format</c> with
    /// <c>-o</c> alias (per Epic AN's shared-flag contract). Defaults
    /// to <see cref="OutputFormat.Table"/>; explicitly enumerated values
    /// so a typo gets caught by the parser, not by the formatter.
    /// </summary>
    public static Option<OutputFormat> Create()
    {
        var option = new Option<OutputFormat>(
            aliases: new[] { "--format", "-o" },
            description: "Output format: table (default) or json.",
            getDefaultValue: () => OutputFormat.Table);
        option.FromAmong("table", "Table", "json", "Json");
        return option;
    }
}
